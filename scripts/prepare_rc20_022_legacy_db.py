import sqlite3
import sys

database = sys.argv[1]
with sqlite3.connect(database) as connection:
    connection.execute("DROP INDEX IF EXISTS IX_users_Username")
    connection.execute("ALTER TABLE users DROP COLUMN Username")
    connection.execute("CREATE UNIQUE INDEX IX_users_Email ON users(Email)")
    connection.commit()
