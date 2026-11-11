"""Local, read-only Python preview of the FSM API."""
import argparse
import base64
from contextlib import closing
from datetime import date, datetime, timezone
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import re
import secrets
import sqlite3
from threading import Lock
from time import monotonic
from urllib.parse import urlsplit
from uuid import UUID


def database(path):
    db = sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)
    db.row_factory = sqlite3.Row
    return closing(db)


def fields(row, *names):
    return {name[0].lower() + name[1:]: row[name] for name in names}


def verify(password, stored):
    try:
        salt, expected = (base64.b64decode(p, validate=True) for p in stored.split("."))
        return hmac.compare_digest(expected, hashlib.pbkdf2_hmac(
            "sha256", password.encode(), salt, 100_000, 32))
    except (ValueError, AttributeError):
        return False


def equipment(row, today):
    item = fields(row, "Id", "VesselId", "EquipmentName", "Maker", "Model",
                  "SerialNumber", "Quantity", "ExpiryDate", "Responsible", "Remarks")
    days = (date.fromisoformat(row["ExpiryDate"]) - today).days if row["ExpiryDate"] else None
    item.update(category={"RadioAndNavigation": "Radio & Navigation"}.get(
        row["Category"], row["Category"]),
        inspectionBy="Service Company" if row["InspectionBy"] == "ServiceCompany" else "Crew",
        status="OK" if days is None or days > 60 else "Overdue" if days < 0 else "Soon",
        reportAvailable=bool(row["CurrentReportPath"]), reportFileName=row["CurrentReportFileName"],
        certificateAvailable=bool(row["CurrentCertificatePath"]),
        certificateFileName=row["CurrentCertificateFileName"])
    return item


class Preview:
    def __init__(self, path, edition, ship_id=None):
        self.path = path
        self.edition = edition
        self.ship_id = str(UUID(ship_id)) if ship_id else None
        self.lock = Lock()
        self.tokens = {}
        self.failures = {}

    def login(self, db, body):
        username = body.get("username") or body.get("email")
        password = body.get("password")
        if not isinstance(username, str) or not isinstance(password, str):
            return 401, {"message": "Invalid username or password."}
        username = username.strip().lower()
        now = monotonic()
        with self.lock:
            self.failures = {k: v for k, v in self.failures.items() if v[1] > now}
            count, expiry = self.failures.get(username, (0, now + 900))
            if count >= 5 or len(self.failures) >= 1000:
                return 429, {"message": "Try again later."}
            user = db.execute("SELECT * FROM users WHERE lower(Username)=? AND IsDeleted=0",
                              (username,)).fetchone()
            if not user or not user["IsActive"] or user["IsLocked"] or not verify(password, user["PasswordHash"]):
                self.failures[username] = (count + 1, expiry)
                return 401, {"message": "Invalid username or password."}
            allowed = ("Crew",) if self.edition == "Ship" else ("OfficeAdmin", "Superintendent")
            if user["Role"] not in allowed:
                return 403, {"message": "Account is not permitted in this edition."}
            self.failures.pop(username, None)
            self.tokens = {k: v for k, v in self.tokens.items() if v[1] > now}
            token = secrets.token_urlsafe(32)
            self.tokens[token] = (user["Id"], now + 3600)
        return 200, dict(fields(user, "Username", "FullName", "Email", "Role"),
                         token=token, userId=user["Id"], edition=self.edition, forcePasswordChange=False)

    def vessels(self, db, user):
        sql = "SELECT v.* FROM vessels v WHERE v.IsDeleted=0"
        params = ()
        if self.edition == "Ship":
            sql += " AND lower(v.Id)=?"
            params = (self.ship_id,)
        elif user["Role"] != "OfficeAdmin":
            sql += (" AND EXISTS (SELECT 1 FROM user_vessels a WHERE a.VesselId=v.Id"
                    " AND a.UserId=? AND a.IsDeleted=0)")
            params = (user["Id"],)
        return db.execute(sql + " ORDER BY v.VesselName, v.Id", params).fetchall()

    def request(self, method, path, authorization="", body=None):
        with database(self.path) as db:
            if method == "GET" and path == "/api/synchronization/activation/state":
                vessel = None
                if self.ship_id:
                    vessel = db.execute("SELECT * FROM vessels WHERE lower(Id)=? AND IsDeleted=0",
                                        (self.ship_id,)).fetchone()
                return 200, {"edition": self.edition, "requiresActivation": False,
                             "vessel": dict(fields(vessel, "VesselName", "ImoNumber"),
                                            vesselId=vessel["Id"]) if vessel else None}
            if method == "POST" and path == "/api/auth/login":
                return self.login(db, body or {})
            with self.lock:
                session = self.tokens.get(authorization.removeprefix("Bearer ")) if authorization.startswith("Bearer ") else None
            if not session or session[1] <= monotonic():
                return 401, {"message": "Sign in first."}
            user = db.execute("SELECT * FROM users WHERE Id=? AND IsDeleted=0 AND IsActive=1 AND IsLocked=0",
                              (session[0],)).fetchone()
            allowed = ("Crew",) if self.edition == "Ship" else ("OfficeAdmin", "Superintendent")
            if not user or user["Role"] not in allowed:
                return 401, {"message": "Sign in again."}
            if method != "GET":
                return 501, {"message": "This Python preview does not write to the database."}
            if path == "/api/auth/me":
                return 200, fields(user, "FullName")
            vessels = self.vessels(db, user)
            if path == "/api/vessels":
                return 200, [dict(fields(v, "Id", "VesselName", "ImoNumber", "Flag",
                                         "CompanyName", "InvoicingDetails", "AccountingEmail"),
                                  displayName=f'{v["VesselName"]} (IMO {v["ImoNumber"]})')
                             for v in vessels]
            today = datetime.now(timezone.utc).date()

            def items(vessel):
                return [equipment(r, today) for r in db.execute(
                    "SELECT * FROM equipment WHERE VesselId=? AND IsDeleted=0"
                    " ORDER BY Category, EquipmentName, Maker, Model", (vessel["Id"],))]

            match = re.fullmatch(r"/api/vessels/([^/]+)/equipment", path)
            if match:
                vessel = next((v for v in vessels if v["Id"].lower() == match[1].lower()), None)
                return (200, items(vessel)) if vessel else (403, {"message": "Access denied."})
            if path == "/api/dashboard":
                def counts(rows):
                    return dict(equipment=len(rows), **{
                        status.lower(): sum(i["status"] == status for i in rows)
                        for status in ("OK", "Soon", "Overdue")})
                all_items, upcoming, vessel_rows = [], [], []
                for v in vessels:
                    rows = items(v)
                    all_items.extend(rows)
                    vessel_rows.append(dict(fields(v, "Id", "VesselName", "ImoNumber", "Flag"), **counts(rows)))
                    for item in rows:
                        if item["expiryDate"]:
                            upcoming.append(dict(item, **fields(v, "VesselName", "ImoNumber"),
                                daysLeft=(date.fromisoformat(item["expiryDate"]) - today).days))
                upcoming.sort(key=lambda i: (i["expiryDate"], i["equipmentName"]))
                return 200, dict(vessels=len(vessels), **counts(all_items), vesselRows=vessel_rows,
                                 upcoming=upcoming[:20], technicalSuperintendents=[], synchronization=[])
            return 501, {"message": "This endpoint has not been ported to Python."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", type=Path, required=True)
    parser.add_argument("--edition", choices=("Office", "Ship"), default="Office")
    parser.add_argument("--ship-id")
    parser.add_argument("--port", type=int, default=8081)
    args = parser.parse_args()
    if not args.db.is_file() or (args.edition == "Ship" and not args.ship_id):
        parser.error("An existing database is required; Ship also requires --ship-id.")
    api = Preview(args.db, args.edition, args.ship_id)

    class Handler(BaseHTTPRequestHandler):
        def handle_api(self):
            try:
                body = None
                if self.command == "POST":
                    length = int(self.headers.get("Content-Length", "0"))
                    if not 0 <= length <= 16384:
                        raise ValueError()
                    body = json.loads(self.rfile.read(length)) if length else {}
                    if not isinstance(body, dict):
                        raise ValueError()
                status, payload = api.request(self.command, urlsplit(self.path).path,
                                              self.headers.get("Authorization", ""), body)
            except (ValueError, UnicodeError):
                status, payload = 400, {"message": "Invalid request."}
            except sqlite3.Error:
                status, payload = 500, {"message": "Unable to read the FSM database."}
            data = json.dumps(payload).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)

        do_GET = do_POST = do_PUT = do_PATCH = do_DELETE = handle_api

    with ThreadingHTTPServer(("127.0.0.1", args.port), Handler) as server:
        print(f"FSM Python API preview: http://127.0.0.1:{args.port}", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
