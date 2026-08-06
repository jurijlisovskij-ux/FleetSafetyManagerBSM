using Microsoft.Data.Sqlite;

if (args.Length != 1)
    throw new ArgumentException("Pass the RC19 fixture database path.");

var databasePath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
if (File.Exists(databasePath))
    File.Delete(databasePath);

await using var connection = new SqliteConnection($"Data Source={databasePath}");
await connection.OpenAsync();
await using var command = connection.CreateCommand();
command.CommandText = """
    PRAGMA foreign_keys = ON;
    CREATE TABLE users (
      Id TEXT NOT NULL PRIMARY KEY,
      FullName TEXT NOT NULL,
      Email TEXT NOT NULL,
      PasswordHash TEXT NOT NULL,
      Role TEXT NOT NULL,
      IsActive INTEGER NOT NULL DEFAULT 1
    );
    CREATE UNIQUE INDEX IX_users_Email ON users(Email);

    CREATE TABLE vessels (
      Id TEXT NOT NULL PRIMARY KEY,
      VesselName TEXT NOT NULL,
      ImoNumber TEXT NOT NULL,
      Flag TEXT NULL
    );
    CREATE UNIQUE INDEX IX_vessels_ImoNumber ON vessels(ImoNumber);

    CREATE TABLE equipment (
      Id TEXT NOT NULL PRIMARY KEY,
      VesselId TEXT NOT NULL,
      Category TEXT NOT NULL,
      EquipmentName TEXT NOT NULL,
      Maker TEXT NULL,
      Model TEXT NULL,
      SerialNumber TEXT NULL,
      Quantity INTEGER NOT NULL DEFAULT 1,
      InspectionBy TEXT NOT NULL,
      ExpiryDate TEXT NULL,
      Remarks TEXT NULL,
      FOREIGN KEY (VesselId) REFERENCES vessels(Id) ON DELETE CASCADE
    );

    CREATE TABLE user_vessels (
      UserId TEXT NOT NULL,
      VesselId TEXT NOT NULL,
      PRIMARY KEY (UserId, VesselId),
      FOREIGN KEY (UserId) REFERENCES users(Id) ON DELETE CASCADE,
      FOREIGN KEY (VesselId) REFERENCES vessels(Id) ON DELETE CASCADE
    );

    INSERT INTO vessels (Id, VesselName, ImoNumber, Flag)
      VALUES ('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',
              'MV RC19 UPGRADE', '9123456', 'Lithuania');
    """;
await command.ExecuteNonQueryAsync();

Console.WriteLine(databasePath);
