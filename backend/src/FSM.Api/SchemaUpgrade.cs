using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api;

public static class SchemaUpgrade
{
    public static async Task ApplyAsync(FsmDbContext db)
    {
        await db.Database.OpenConnectionAsync();

        try
        {
            await AddColumnIfMissingAsync(
                db, "equipment", "CurrentReportPath", "TEXT");
            await AddColumnIfMissingAsync(
                db, "equipment", "CurrentReportFileName", "TEXT");
            await AddColumnIfMissingAsync(
                db, "equipment", "CurrentCertificatePath", "TEXT");
            await AddColumnIfMissingAsync(
                db, "equipment", "CurrentCertificateFileName", "TEXT");
            await AddColumnIfMissingAsync(
                db, "equipment", "Responsible", "TEXT");
            await AddColumnIfMissingAsync(
                db, "vessels", "CompanyName", "TEXT");
            await AddColumnIfMissingAsync(
                db, "vessels", "InvoicingDetails", "TEXT");
            await AddColumnIfMissingAsync(
                db, "vessels", "AccountingEmail", "TEXT");
            await AddSynchronizationColumnsAsync(db, "users");
            await AddSynchronizationColumnsAsync(db, "vessels");
            await AddSynchronizationColumnsAsync(db, "equipment");
            await AddSynchronizationColumnsAsync(db, "user_vessels");
            await AddColumnIfMissingAsync(db, "users", "ForcePasswordChange", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(db, "users", "IsLocked", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(db, "users", "FailedLoginAttempts", "INTEGER NOT NULL DEFAULT 0");
            await CreateSynchronizationTablesAsync(db);
            await CreateSecurityTablesAsync(db);
            await CreateMedicineTablesAsync(db);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task CreateMedicineTablesAsync(FsmDbContext db)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS medicine_items (
              Id TEXT NOT NULL PRIMARY KEY, VesselId TEXT NOT NULL, ItemName TEXT NOT NULL,
              FormStrength TEXT NULL, RequiredQuantity TEXT NOT NULL, ActualQuantity TEXT NOT NULL,
              Unit TEXT NOT NULL, BatchNumber TEXT NULL, ExpiryDate TEXT NULL, StorageLocation TEXT NULL,
              MedicalCategory TEXT NULL, IsDangerousGoods INTEGER NOT NULL, ReplacementRequired INTEGER NOT NULL,
              Remarks TEXT NULL, IsActive INTEGER NOT NULL, IsStandardTemplateItem INTEGER NOT NULL,
              LastModifiedUtc TEXT NOT NULL, IsDeleted INTEGER NOT NULL DEFAULT 0,
              FOREIGN KEY (VesselId) REFERENCES vessels(Id) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_medicine_items_identity
              ON medicine_items(VesselId, ItemName, FormStrength, IsDangerousGoods);
            CREATE TABLE IF NOT EXISTS vessel_medicine_configuration (
              VesselId TEXT NOT NULL PRIMARY KEY, CategoryAEnabled INTEGER NOT NULL,
              CategoryBEnabled INTEGER NOT NULL, DangerousGoodsEnabled INTEGER NOT NULL,
              WarningDays INTEGER NOT NULL, LastModifiedUtc TEXT NOT NULL,
              IsDeleted INTEGER NOT NULL DEFAULT 0,
              FOREIGN KEY (VesselId) REFERENCES vessels(Id) ON DELETE CASCADE);
            """;
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task CreateSecurityTablesAsync(FsmDbContext db)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS audit_logs (
                Id TEXT NOT NULL PRIMARY KEY,
                DateUtc TEXT NOT NULL,
                Action TEXT NOT NULL,
                Operator TEXT NOT NULL,
                TargetUser TEXT NULL,
                ComputerName TEXT NOT NULL,
                Result TEXT NOT NULL,
                Summary TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_audit_logs_DateUtc ON audit_logs (DateUtc);
            """;
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task AddSynchronizationColumnsAsync(
        FsmDbContext db,
        string tableName)
    {
        await AddColumnIfMissingAsync(
            db,
            tableName,
            "LastModifiedUtc",
            "TEXT NOT NULL DEFAULT '1970-01-01 00:00:00'");
        await AddColumnIfMissingAsync(
            db,
            tableName,
            "IsDeleted",
            "INTEGER NOT NULL DEFAULT 0");
    }

    private static async Task CreateSynchronizationTablesAsync(
        FsmDbContext db)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS synchronization_settings (
                VesselId TEXT NOT NULL PRIMARY KEY,
                RemoteBaseUrl TEXT NULL,
                TokenHash TEXT NULL,
                ProtectedToken TEXT NULL,
                AutomaticIntervalHours INTEGER NOT NULL DEFAULT 24,
                LastUploadedUtc TEXT NOT NULL DEFAULT '1970-01-01 00:00:00',
                RemoteCursorUtc TEXT NOT NULL DEFAULT '1970-01-01 00:00:00',
                LastModifiedUtc TEXT NOT NULL DEFAULT '1970-01-01 00:00:00',
                LastAttemptUtc TEXT NULL,
                LastSynchronizationUtc TEXT NULL,
                LastStatus TEXT NOT NULL DEFAULT 'Warning',
                CurrentStage TEXT NOT NULL DEFAULT 'Idle',
                LastError TEXT NULL,
                FOREIGN KEY (VesselId) REFERENCES vessels (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS synchronization_logs (
                Id TEXT NOT NULL PRIMARY KEY,
                VesselId TEXT NOT NULL,
                DateUtc TEXT NOT NULL,
                Direction TEXT NOT NULL,
                DurationMilliseconds INTEGER NOT NULL,
                UploadedItems INTEGER NOT NULL,
                DownloadedItems INTEGER NOT NULL,
                Result TEXT NOT NULL,
                ErrorMessage TEXT NULL,
                FOREIGN KEY (VesselId) REFERENCES vessels (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_synchronization_logs_VesselId_DateUtc
                ON synchronization_logs (VesselId, DateUtc);

            CREATE TABLE IF NOT EXISTS synchronization_transfers (
                Id TEXT NOT NULL PRIMARY KEY,
                VesselId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                Checksum TEXT NOT NULL,
                TotalBytes INTEGER NOT NULL,
                ReceivedBytes INTEGER NOT NULL,
                TemporaryPath TEXT NOT NULL,
                FileLastModifiedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_synchronization_transfers_Vessel_Path_Checksum
                ON synchronization_transfers (VesselId, RelativePath, Checksum);
            """;

        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task AddColumnIfMissingAsync(
        FsmDbContext db,
        string tableName,
        string columnName,
        string columnType)
    {
        var connection = db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(
                reader.GetString(1),
                columnName,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE \"{tableName}\" " +
            $"ADD COLUMN \"{columnName}\" {columnType};");
    }
}
