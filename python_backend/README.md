# FSM Python API preview

This is the first migration slice, for local development only. It reads a copy of
the existing FSM SQLite database and runs on loopback port 8081. Python 3.10+
is required. No third-party packages are required.

From this directory:

```text
python server.py --db PATH_TO_FSM_DATABASE_COPY
python server.py --db PATH_TO_SHIP_DATABASE_COPY --edition Ship --ship-id VESSEL_UUID
python -m unittest -v
```

Available: sign-in with existing password hashes, account and vessel scopes,
vessel list, equipment list and dashboard counts. Obtain a temporary bearer
token via POST /api/auth/login with JSON username and password. Supply it in
Authorization: Bearer TOKEN for subsequent requests. The server opens the database read-only by default. Sessions are local to this preview and expire in one hour.

This preview does not replace the Windows installations. To try the existing React frontend with Python, build it with npm ci and npm run build in frontend (leave VITE_API_BASE_URL unset), then run from python_backend:

```text
python server.py --db PATH_TO_FSM_DATABASE_COPY --write-test-copy --frontend ../frontend/dist
```

Open http://127.0.0.1:8081. The flag permits adding and editing equipment. Always use a separate copy, since it writes to the path provided.

Other write operations, synchronization,
administration, documents, reports and installer integration still need to be ported.
