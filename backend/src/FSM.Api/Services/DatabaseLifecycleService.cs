using FSM.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Services;

public static class SystemVersions
{
    public const string ApplicationVersion = "1.0.0-rc1";
    public const string InitialSchemaVersion = "202607240000_InitialCoreSchema";
    public const string MinimumSupportedDatabaseVersion = InitialSchemaVersion;
    public const string MaximumSupportedDatabaseVersion =
        "20260726080125_AddSystemAdministration";
}

public sealed class DatabaseLifecycleStatus
{
    public string Mode { get; internal set; } = "Invalid";
    public string StartupMigrationResult { get; internal set; } = "Not started";
    public string? CurrentSchemaVersion { get; internal set; }
    public int PendingMigrationCount { get; internal set; }
    public bool MigrationLockActive { get; internal set; }
}

public sealed class DatabaseLifecycleService(
    FsmDbContext db, SystemBackupService backup,
    DatabaseLifecycleStatus status,
    ILogger<DatabaseLifecycleService> logger)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        var databasePath = backup.DatabasePath;
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("Database directory is invalid.");
        Directory.CreateDirectory(directory);
        VerifyWritable(directory);
        var lockPath = $"{databasePath}.migration.lock";
        FileStream? migrationLock = null;
        try
        {
            migrationLock = new FileStream(lockPath, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
            status.MigrationLockActive = true;
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another application instance holds the database migration lock.", exception);
        }
        try
        {
            var state = await DetectAsync(databasePath, ct);
            status.Mode = state;
            logger.LogInformation("FSM database lifecycle mode selected: {Mode}", state);
            if (state == "LegacyBaselined")
                await BaselineLegacyAsync(ct);
            var known = db.Database.GetMigrations().ToList();
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            if (applied.Count > 0 &&
                !applied.Contains(SystemVersions.InitialSchemaVersion,
                    StringComparer.Ordinal))
            {
                await ValidateCoreTablesAsync(databasePath, ct);
                await BaselineLegacyAsync(ct);
                applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
                logger.LogWarning(
                    "Existing migration history was bridged with initial core baseline {Baseline}.",
                    SystemVersions.InitialSchemaVersion);
            }
            var unknown = applied.Where(x => !known.Contains(x, StringComparer.Ordinal)).ToList();
            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    $"Database schema is newer or unsupported: {string.Join(", ", unknown)}.");
            status.PendingMigrationCount = known.Except(applied).Count();
            await db.Database.MigrateAsync(ct);
            var remaining = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (remaining.Count > 0)
                throw new InvalidOperationException("Database migration did not complete.");
            status.CurrentSchemaVersion = (await db.Database.GetAppliedMigrationsAsync(ct))
                .LastOrDefault();
            status.PendingMigrationCount = 0;
            status.StartupMigrationResult = "Success";
            logger.LogInformation(
                "FSM database migration completed. Application {ApplicationVersion}, schema {SchemaVersion}.",
                SystemVersions.ApplicationVersion, status.CurrentSchemaVersion);
        }
        catch
        {
            status.Mode = "Invalid";
            status.StartupMigrationResult = "Failed";
            throw;
        }
        finally
        {
            status.MigrationLockActive = false;
            if (migrationLock is not null) await migrationLock.DisposeAsync();
            if (File.Exists(lockPath)) File.Delete(lockPath);
        }
    }

    private async Task<string> DetectAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return "New";
        await using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadWrite;Pooling=False");
        await connection.OpenAsync(ct);
        var tables = await Names(connection, "table", ct);
        if (tables.Count == 0) return "New";
        if (tables.Contains("__EFMigrationsHistory"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('__EFMigrationsHistory') WHERE name IN ('MigrationId','ProductVersion');";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 2)
                throw new InvalidOperationException("Migration history is malformed.");
            return "Migrated";
        }
        var required = new Dictionary<string, string[]>
        {
            ["users"] = ["Id", "FullName", "Email", "PasswordHash", "Role", "IsActive"],
            ["vessels"] = ["Id", "VesselName", "ImoNumber"],
            ["user_vessels"] = ["UserId", "VesselId"],
            ["equipment"] = ["Id", "VesselId", "Category", "EquipmentName",
                "Quantity", "InspectionBy", "CurrentCertificatePath", "CurrentReportPath"]
        };
        foreach (var signature in required)
        {
            if (!tables.Contains(signature.Key))
                throw new InvalidOperationException(
                    $"Unknown legacy database schema: required table '{signature.Key}' is missing.");
            var columns = await Columns(connection, signature.Key, ct);
            if (signature.Value.Any(x => !columns.Contains(x)))
                throw new InvalidOperationException(
                    $"Unknown legacy database schema: table '{signature.Key}' does not match.");
        }
        return "LegacyBaselined";
    }

    private async Task BaselineLegacyAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
              "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
              "ProductVersion" TEXT NOT NULL
            );
            """, ct);
        await db.Database.ExecuteSqlRawAsync(
            """INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId","ProductVersion") VALUES ({0},{1});""",
            [SystemVersions.InitialSchemaVersion, "9.0.0"], ct);
        await transaction.CommitAsync(ct);
        logger.LogWarning("Validated legacy database was transactionally baselined at {Baseline}.",
            SystemVersions.InitialSchemaVersion);
    }

    private static async Task ValidateCoreTablesAsync(string path, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadWrite;Pooling=False");
        await connection.OpenAsync(ct);
        var tables = await Names(connection, "table", ct);
        foreach (var table in new[] { "users", "vessels", "user_vessels", "equipment" })
            if (!tables.Contains(table))
                throw new InvalidOperationException(
                    $"Existing migration history is incompatible: '{table}' is missing.");
    }

    private static async Task<HashSet<string>> Names(
        SqliteConnection connection, string type, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type=$type AND name NOT LIKE 'sqlite_%';";
        command.Parameters.AddWithValue("$type", type);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }
    private static async Task<HashSet<string>> Columns(
        SqliteConnection connection, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(1));
        return result;
    }
    private static void VerifyWritable(string directory)
    {
        var probe = Path.Combine(directory, $".fsm-write-{Guid.NewGuid():N}");
        try { File.WriteAllText(probe, ""); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }
}
