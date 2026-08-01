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
                db, "vessels", "CompanyName", "TEXT");
            await AddColumnIfMissingAsync(
                db, "vessels", "InvoicingDetails", "TEXT");
            await AddColumnIfMissingAsync(
                db, "vessels", "AccountingEmail", "TEXT");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
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
