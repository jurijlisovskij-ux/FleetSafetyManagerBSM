using FSM.Api.Services;
using FSM.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FSM.Api.IntegrationTests;

public sealed class DatabaseLifecycleTests
{
    [Fact]
    public async Task New_database_is_created_by_migrations_with_history()
    {
        await WithLifecycle(async (service, db, status, path) =>
        {
            await service.InitializeAsync(default);
            Assert.Equal("New", status.Mode);
            Assert.Equal("Success", status.StartupMigrationResult);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(SystemVersions.InitialSchemaVersion,
                await db.Database.GetAppliedMigrationsAsync());
            Assert.True(await db.SystemSettings.AnyAsync() == false);
            Assert.False(File.Exists($"{path}.migration.lock"));
        });
    }

    [Fact]
    public async Task Valid_legacy_database_is_baselined_and_data_is_preserved()
    {
        await WithLifecycle(async (service, db, status, _) =>
        {
            await CreateLegacyCore(db.Database.GetConnectionString()!);
            await service.InitializeAsync(default);
            Assert.Equal("LegacyBaselined", status.Mode);
            Assert.Equal("Legacy Vessel",
                (await db.Vessels.AsNoTracking().SingleAsync()).VesselName);
            Assert.Contains(SystemVersions.InitialSchemaVersion,
                await db.Database.GetAppliedMigrationsAsync());
        });
    }

    [Fact]
    public async Task Unknown_legacy_and_newer_schema_fail_safely()
    {
        await WithLifecycle(async (service, db, _, path) =>
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE unknown_table (Id TEXT);");
            await db.Database.CloseConnectionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InitializeAsync(default));
            Assert.False(File.Exists($"{path}.migration.lock"));
        });
        await WithLifecycle(async (service, db, _, _) =>
        {
            await service.InitializeAsync(default);
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "__EFMigrationsHistory" VALUES ('999999999999_Future','99.0');""");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InitializeAsync(default));
        });
    }

    [Fact]
    public async Task Existing_startup_lock_blocks_second_migrator()
    {
        await WithLifecycle(async (service, _, _, path) =>
        {
            await File.WriteAllTextAsync($"{path}.migration.lock", "held");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InitializeAsync(default));
            Assert.Contains("migration lock", error.Message);
            File.Delete($"{path}.migration.lock");
        });
    }

    private static async Task WithLifecycle(Func<DatabaseLifecycleService,
        FsmDbContext, DatabaseLifecycleStatus, string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "fsm-lifecycle-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "fsm.db");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = $"Data Source={path}",
                    ["Storage:RootPath"] = Path.Combine(root, "documents"),
                    ["Administration:BackupPath"] = Path.Combine(root, "backups")
                }).Build();
            var options = new DbContextOptionsBuilder<FsmDbContext>()
                .UseSqlite($"Data Source={path}").Options;
            await using var db = new FsmDbContext(options);
            var status = new DatabaseLifecycleStatus();
            var service = new DatabaseLifecycleService(db,
                new SystemBackupService(configuration), status,
                NullLogger<DatabaseLifecycleService>.Instance);
            await action(service, db, status, path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task CreateLegacyCore(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE users (Id TEXT PRIMARY KEY, FullName TEXT, Email TEXT,
              PasswordHash TEXT, Role TEXT, IsActive INTEGER);
            CREATE TABLE vessels (Id TEXT PRIMARY KEY, VesselName TEXT, ImoNumber TEXT,
              Flag TEXT, CompanyName TEXT, InvoicingDetails TEXT, AccountingEmail TEXT);
            CREATE TABLE user_vessels (UserId TEXT, VesselId TEXT);
            CREATE TABLE equipment (Id TEXT PRIMARY KEY, VesselId TEXT, Category TEXT,
              EquipmentName TEXT, Maker TEXT, Model TEXT, SerialNumber TEXT,
              Quantity INTEGER, InspectionBy TEXT, ExpiryDate TEXT, Remarks TEXT,
              CurrentCertificatePath TEXT, CurrentCertificateFileName TEXT,
              CurrentReportPath TEXT, CurrentReportFileName TEXT);
            INSERT INTO vessels (Id,VesselName,ImoNumber) VALUES
              ('10000000-0000-0000-0000-000000000001','Legacy Vessel','9000001');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
