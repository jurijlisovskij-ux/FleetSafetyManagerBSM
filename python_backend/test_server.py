from datetime import date, timedelta
import base64
import hashlib
from pathlib import Path
import sqlite3
import tempfile
import unittest
from uuid import uuid4

from server import Preview


class PreviewTest(unittest.TestCase):
    def test_roles_and_read_only_register(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "fsm.db"
            db = sqlite3.connect(path)
            db.executescript("""
            CREATE TABLE users (Id, Username, FullName, Email, Role, IsDeleted, IsActive, IsLocked, PasswordHash);
            CREATE TABLE vessels (Id, VesselName, ImoNumber, Flag, CompanyName, InvoicingDetails, AccountingEmail, IsDeleted);
            CREATE TABLE user_vessels (UserId, VesselId, IsDeleted);
            CREATE TABLE equipment (Id, VesselId, EquipmentName, Maker, Model, SerialNumber, Quantity, ExpiryDate,
                Responsible, Remarks, Category, InspectionBy, CurrentReportPath, CurrentReportFileName,
                CurrentCertificatePath, CurrentCertificateFileName, IsDeleted, LastModifiedUtc);
            """)
            salt = bytes(range(16))
            digest = hashlib.pbkdf2_hmac("sha256", b"test password", salt, 100_000)
            stored = ".".join(base64.b64encode(p).decode() for p in (salt, digest))
            for user, role in (("admin", "OfficeAdmin"), ("super", "Superintendent"), ("crew", "Crew")):
                db.execute("INSERT INTO users VALUES (?,?,?,?,?,0,1,0,?)",
                           (user, user, user, "", role, stored))
            a, b = str(uuid4()), str(uuid4())
            for vid, name, imo in ((a, "Figaro", "1234567"), (b, "Other", "7654321")):
                db.execute("INSERT INTO vessels VALUES (?,?,?,NULL,NULL,NULL,NULL,0)", (vid, name, imo))
            db.execute("INSERT INTO user_vessels VALUES ('super',?,0)", (a,))
            db.execute("""INSERT INTO equipment VALUES ('e',?,'VHF',NULL,NULL,NULL,1,?,
                NULL,NULL,'RadioAndNavigation','Crew',NULL,NULL,NULL,NULL,0,NULL)""",
                       (a, (date.today() - timedelta(days=2)).isoformat()))
            db.commit()
            db.close()
            before = path.read_bytes()

            def sign_in(api, username):
                code, payload = api.request("POST", "/api/auth/login",
                                            body=dict(username=username, password="test password"))
                self.assertEqual(code, 200)
                return "Bearer " + payload["token"]

            office = Preview(path, "Office")
            self.assertEqual(office.request("GET", "/api/dashboard")[0], 401)
            admin = sign_in(office, "admin")
            self.assertEqual(len(office.request("GET", "/api/vessels", admin)[1]), 2)
            superintendent = sign_in(office, "super")
            self.assertEqual(office.request("GET", f"/api/vessels/{b}/equipment", superintendent)[0], 403)
            dashboard = office.request("GET", "/api/dashboard", superintendent)[1]
            self.assertEqual((dashboard["vessels"], dashboard["overdue"]), (1, 1))
            ship = Preview(path, "Ship", a)
            crew = sign_in(ship, "crew")
            self.assertEqual(ship.request("GET", f"/api/vessels/{a}/equipment", crew)[1][0]["category"],
                             "Radio & Navigation")
            self.assertEqual(ship.request("POST", f"/api/vessels/{a}/equipment", crew)[0], 501)
            self.assertEqual(path.read_bytes(), before)
            writer = Preview(path, "Office", writable=True)
            token = sign_in(writer, "super")
            register = f"/api/vessels/{a}/equipment"
            draft = dict(equipmentName=" Fire extinguisher ", quantity=2, category=1,
                         inspectionBy=1, expiryDate="2099-01-01")
            self.assertEqual(writer.request("POST", f"/api/vessels/{b}/equipment", token, draft)[0], 403)
            self.assertEqual(writer.request("POST", register, token, dict(draft, expiryDate="2026-02-30"))[0], 400)
            code, saved = writer.request("POST", register, token, draft)
            self.assertEqual((code, saved["equipmentName"], saved["inspectionBy"]),
                             (201, "Fire extinguisher", "Service Company"))
            detail = f"{register}/{saved['id'].lower()}"
            self.assertEqual(writer.request("PUT", detail, token, dict(draft, quantity=3))[0], 200)
            self.assertEqual(writer.request("PUT", f"{register}/{uuid4()}", token, draft)[0], 404)
            reopened = Preview(path, "Office")
            rows = reopened.request("GET", register, sign_in(reopened, "super"))[1]
            self.assertEqual(next(x["quantity"] for x in rows if x["id"] == saved["id"]), 3)
            with sqlite3.connect(path) as db:
                self.assertTrue(db.execute("SELECT LastModifiedUtc FROM equipment WHERE Id=?",
                                           (saved["id"],)).fetchone()[0])


if __name__ == "__main__":
    unittest.main()
