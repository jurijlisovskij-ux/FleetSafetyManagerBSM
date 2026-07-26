using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FSM.Api.IntegrationTests;

public sealed class MigrationTests
{
    [Fact]
    public async Task Legacy_document_metadata_imports_once_and_indexes_are_created()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "fsm-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var databasePath = Path.Combine(testRoot, "legacy.db");
        var connectionString = $"Data Source={databasePath}";
        var equipmentId = Guid.NewGuid();

        try
        {
            await CreateLegacyDatabaseAsync(connectionString, equipmentId);
            var options = new DbContextOptionsBuilder<FsmDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using (var db = new FsmDbContext(options))
            {
                await db.Database.MigrateAsync();
                await db.Database.MigrateAsync();

                var equipment = await db.Equipment
                    .AsNoTracking()
                    .SingleAsync();
                Assert.Equal(equipmentId, equipment.Id);
                Assert.Equal("Legacy EPIRB", equipment.EquipmentName);

                var documents = await db.EquipmentDocuments
                    .AsNoTracking()
                    .OrderBy(x => x.DocumentType)
                    .ToListAsync();
                Assert.Equal(2, documents.Count);
                Assert.Single(
                    documents,
                    x => x.DocumentType ==
                         EquipmentDocumentType.Certificate);
                Assert.Single(
                    documents,
                    x => x.DocumentType ==
                         EquipmentDocumentType.ServiceReport);
                Assert.All(documents, document => Assert.True(document.IsCurrent));
                Assert.Equal(
                    "legacy/certificate.pdf",
                    documents.Single(x =>
                            x.DocumentType ==
                            EquipmentDocumentType.Certificate)
                        .StoredFileName);
                Assert.Equal(
                    "legacy/report.pdf",
                    documents.Single(x =>
                            x.DocumentType ==
                            EquipmentDocumentType.ServiceReport)
                        .StoredFileName);
            }

            var indexNames = await ReadIndexNamesAsync(connectionString);
            Assert.Contains(
                "IX_equipment_documents_EquipmentId_DocumentType_CreatedUtc",
                indexNames);
            Assert.Contains(
                "IX_equipment_documents_EquipmentId_DocumentType_Current",
                indexNames);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task CreateLegacyDatabaseAsync(
        string connectionString,
        Guid equipmentId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE equipment (
                Id TEXT NOT NULL PRIMARY KEY,
                VesselId TEXT NOT NULL,
                Category TEXT NOT NULL,
                EquipmentName TEXT NOT NULL,
                Maker TEXT NULL,
                Model TEXT NULL,
                SerialNumber TEXT NULL,
                Quantity INTEGER NOT NULL,
                InspectionBy TEXT NOT NULL,
                ExpiryDate TEXT NULL,
                Remarks TEXT NULL,
                CurrentReportPath TEXT NULL,
                CurrentReportFileName TEXT NULL,
                CurrentCertificatePath TEXT NULL,
                CurrentCertificateFileName TEXT NULL
            );
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            INSERT INTO "__EFMigrationsHistory" VALUES
                ('202607240000_InitialCoreSchema', '9.0.0');

            INSERT INTO equipment (
                Id, VesselId, Category, EquipmentName, Maker, Model,
                SerialNumber, Quantity, InspectionBy, ExpiryDate, Remarks,
                CurrentReportPath, CurrentReportFileName,
                CurrentCertificatePath, CurrentCertificateFileName)
            VALUES (
                $equipmentId, $vesselId, 'LSA', 'Legacy EPIRB', NULL, NULL,
                NULL, 1, 'Crew', '2027-01-02', 'Legacy remarks',
                'legacy/report.pdf', 'legacy-report.pdf',
                'legacy/certificate.pdf', 'legacy-certificate.pdf');
            """;
        command.Parameters.AddWithValue(
            "$equipmentId",
            equipmentId.ToString());
        command.Parameters.AddWithValue(
            "$vesselId",
            Guid.NewGuid().ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadIndexNamesAsync(
        string connectionString)
    {
        var names = new List<string>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list('equipment_documents');";
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            names.Add(reader.GetString(1));

        return names;
    }
}
