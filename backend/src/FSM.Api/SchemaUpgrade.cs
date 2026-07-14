using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api;

public static class SchemaUpgrade
{
    public static async Task ApplyAsync(FsmDbContext db)
    {
        const string sql =
            "ALTER TABLE equipment " +
            "ADD COLUMN IF NOT EXISTS \"CurrentReportPath\" text, " +
            "ADD COLUMN IF NOT EXISTS \"CurrentReportFileName\" text, " +
            "ADD COLUMN IF NOT EXISTS \"CurrentCertificatePath\" text, " +
            "ADD COLUMN IF NOT EXISTS \"CurrentCertificateFileName\" text;";

        await db.Database.ExecuteSqlRawAsync(sql);

        const string vesselSql =
            "ALTER TABLE vessels " +
            "ADD COLUMN IF NOT EXISTS \"CompanyName\" text, " +
            "ADD COLUMN IF NOT EXISTS \"InvoicingDetails\" text, " +
            "ADD COLUMN IF NOT EXISTS \"AccountingEmail\" text;";

        await db.Database.ExecuteSqlRawAsync(vesselSql);
    }
}
