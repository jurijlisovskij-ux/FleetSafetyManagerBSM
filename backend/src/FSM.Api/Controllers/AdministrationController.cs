using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FSM.Api.Services;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController]
[Authorize(Roles = "OfficeAdmin")]
[Route("api/administration")]
public sealed class AdministrationController(
    FsmDbContext db,
    IConfiguration configuration,
    AuditService audit) : ControllerBase
{
    private string DatabasePath => db.Database.GetDbConnection().DataSource;
    private string OperatorName => User.FindFirstValue(ClaimTypes.Name)
        ?? User.Identity?.Name ?? "Office administrator";

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken cancellationToken)
    {
        var database = new FileInfo(DatabasePath);
        var lastAudit = await db.AuditLogs.AsNoTracking()
            .OrderByDescending(x => x.DateUtc)
            .Select(x => (DateTime?)x.DateUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new
        {
            applicationVersion = "RC30",
            edition = configuration["Application:Edition"] ?? "Office",
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            machineName = Environment.MachineName,
            databasePath = database.FullName,
            databaseSizeBytes = database.Exists ? database.Length : 0,
            databaseLastWriteUtc = database.Exists ? database.LastWriteTimeUtc : (DateTime?)null,
            lastAuditUtc = lastAudit,
            pendingRestore = System.IO.File.Exists(PendingRestorePath()),
            serverTimeUtc = DateTime.UtcNow
        });
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var databaseOk = await db.Database.CanConnectAsync(cancellationToken);
        var integrity = "unavailable";
        if (databaseOk)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            if (command.Connection?.State != System.Data.ConnectionState.Open)
                await command.Connection!.OpenAsync(cancellationToken);
            integrity = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "unknown";
        }

        return Ok(new
        {
            status = databaseOk && integrity.Equals("ok", StringComparison.OrdinalIgnoreCase)
                ? "Healthy" : "Attention required",
            database = databaseOk ? "Connected" : "Unavailable",
            integrity,
            documentsDirectory = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Data", "Documents")),
            freeSpaceBytes = new DriveInfo(Path.GetPathRoot(DatabasePath)!).AvailableFreeSpace,
            checkedUtc = DateTime.UtcNow
        });
    }

    [HttpGet("backup")]
    public async Task<IActionResult> Backup(CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"FSM-Backup-{Guid.NewGuid():N}.db");
        await using (var source = new SqliteConnection(db.Database.GetConnectionString()))
        await using (var destination = new SqliteConnection($"Data Source={tempPath}"))
        {
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }

        audit.Add("Database backup exported", OperatorName, null);
        await db.SaveChangesAsync(cancellationToken);
        var bytes = await System.IO.File.ReadAllBytesAsync(tempPath, cancellationToken);
        System.IO.File.Delete(tempPath);
        return File(bytes, "application/octet-stream", $"FSM-Backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
    }

    [HttpPost("restore")]
    [RequestSizeLimit(1_073_741_824)]
    public async Task<IActionResult> StageRestore(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0) return BadRequest(new { message = "Select a non-empty FSM database backup." });
        var tempPath = Path.Combine(Path.GetTempPath(), $"FSM-Restore-{Guid.NewGuid():N}.db");
        await using (var output = System.IO.File.Create(tempPath))
            await file.CopyToAsync(output, cancellationToken);

        try
        {
            await ValidateBackupAsync(tempPath, cancellationToken);
            var pendingPath = PendingRestorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
            System.IO.File.Move(tempPath, pendingPath, overwrite: true);
            audit.Add("Database restore staged", OperatorName, null,
                summary: $"{file.FileName}; restart required");
            await db.SaveChangesAsync(cancellationToken);
            return Ok(new { message = "Backup validated. Restart Fleet Safety Manager to apply the restore.", restartRequired = true });
        }
        catch (Exception exception)
        {
            System.IO.File.Delete(tempPath);
            return BadRequest(new { message = $"The selected file is not a valid FSM backup: {exception.Message}" });
        }
    }

    [HttpDelete("restore/pending")]
    public IActionResult CancelRestore()
    {
        var path = PendingRestorePath();
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        return Ok(new { message = "Pending restore cancelled." });
    }

    [HttpPost("maintenance/optimize")]
    public async Task<IActionResult> Optimize(CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA optimize;", cancellationToken);
        audit.Add("Database optimized", OperatorName, null);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Database optimization completed." });
    }

    [HttpDelete("maintenance/audit")]
    public async Task<IActionResult> ClearOldAudit([FromQuery] int retentionDays = 365,
        CancellationToken cancellationToken = default)
    {
        retentionDays = Math.Clamp(retentionDays, 30, 3650);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = await db.AuditLogs.Where(x => x.DateUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        audit.Add("Old audit records removed", OperatorName, null,
            summary: $"Retention {retentionDays} days; removed {deleted}");
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = $"Removed {deleted} audit records older than {retentionDays} days.", deleted });
    }

    [HttpGet("audit/export")]
    public async Task<IActionResult> ExportAudit(CancellationToken cancellationToken)
    {
        var rows = await db.AuditLogs.AsNoTracking().OrderByDescending(x => x.DateUtc)
            .ToListAsync(cancellationToken);
        var csv = new StringBuilder("Date UTC,Action,Operator,Target,Computer,Result,Summary\r\n");
        foreach (var row in rows)
            csv.AppendLine(string.Join(',', new[] { row.DateUtc.ToString("O"), row.Action, row.Operator,
                row.TargetUser, row.ComputerName, row.Result, row.Summary }.Select(Csv)));
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
            "text/csv", $"FSM-Audit-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("diagnostics")]
    public async Task<IActionResult> Diagnostics(CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var summary = archive.CreateEntry("system-summary.json");
            await using var writer = new StreamWriter(summary.Open());
            await writer.WriteAsync(JsonSerializer.Serialize(new
            {
                applicationVersion = "RC30",
                edition = configuration["Application:Edition"] ?? "Office",
                machineName = Environment.MachineName,
                os = Environment.OSVersion.ToString(),
                framework = Environment.Version.ToString(),
                databaseSizeBytes = new FileInfo(DatabasePath).Length,
                generatedUtc = DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return File(stream.ToArray(), "application/zip", $"FSM-Diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
    }

    private string PendingRestorePath() => DatabasePath + ".restore-pending";
    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private static async Task ValidateBackupAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('users','vessels','equipment');";
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        if (!new[] { "users", "vessels", "equipment" }.All(tables.Contains))
            throw new InvalidDataException("required FSM tables were not found");
    }
}
