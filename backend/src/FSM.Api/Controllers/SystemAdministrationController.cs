using System.Globalization;
using System.Security.Claims;
using System.Text;
using ClosedXML.Excel;
using FSM.Api.Services;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public sealed record SystemSettingsRequest(
    string CompanyName, string? CompanyLogo, string DefaultPdfFooter,
    int NotificationDays, int InspectionDraftWarningDays,
    long MaximumDocumentUploadBytes, long MaximumInspectionPhotoBytes,
    long MaximumServiceRequestUploadBytes, string DocumentStoragePath,
    string TimeZone, string DateFormat);

[ApiController, Authorize(Roles = "OfficeAdmin")]
[Route("api/administration")]
public sealed class SystemAdministrationController(
    FsmDbContext db, IConfiguration configuration,
    SystemBackupService backups, SystemAuditService audit,
    ActiveRequestTracker requests, DatabaseLifecycleStatus lifecycle) : ControllerBase
{
    private static readonly string[] SettingKeys =
    [
        "CompanyName", "CompanyLogo", "DefaultPdfFooter", "NotificationDays",
        "InspectionDraftWarningDays", "MaximumDocumentUploadBytes",
        "MaximumInspectionPhotoBytes", "MaximumServiceRequestUploadBytes",
        "DocumentStoragePath", "TimeZone", "DateFormat"
    ];

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        var stored = await db.SystemSettings.AsNoTracking()
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        string Value(string key, string fallback) =>
            stored.TryGetValue(key, out var value) ? value : fallback;
        return Ok(new
        {
            companyName = Value("CompanyName",
                configuration["Inspections:CompanyTitle"] ?? "Fleet Safety Manager"),
            companyLogo = Value("CompanyLogo", ""),
            defaultPdfFooter = Value("DefaultPdfFooter", ""),
            notificationDays = Number(Value("NotificationDays", "30"), 30),
            inspectionDraftWarningDays = Number(Value("InspectionDraftWarningDays",
                configuration["FleetDashboard:DraftInspectionAgeDays"] ?? "14"), 14),
            maximumDocumentUploadBytes = Long(Value("MaximumDocumentUploadBytes",
                configuration["Storage:MaximumUploadSizeBytes"] ?? "26214400"), 26214400),
            maximumInspectionPhotoBytes = Long(Value("MaximumInspectionPhotoBytes",
                configuration["Inspections:MaximumPhotoSizeBytes"] ?? "15728640"), 15728640),
            maximumServiceRequestUploadBytes = Long(Value("MaximumServiceRequestUploadBytes",
                configuration["ServiceRequests:MaximumAttachmentSizeBytes"] ?? "26214400"), 26214400),
            documentStoragePath = Value("DocumentStoragePath",
                configuration["Storage:RootPath"] ?? "Documents"),
            timeZone = Value("TimeZone", "UTC"),
            dateFormat = Value("DateFormat", "dd MMM yyyy"),
            restartRequiredFor = new[]
            {
                "Company logo/PDF settings", "upload limits", "document storage path",
                "time zone"
            }
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings(
        SystemSettingsRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CompanyName) ||
            request.NotificationDays is < 1 or > 365 ||
            request.InspectionDraftWarningDays is < 1 or > 365 ||
            request.MaximumDocumentUploadBytes is < 1024 or > 1073741824 ||
            request.MaximumInspectionPhotoBytes is < 1024 or > 1073741824 ||
            request.MaximumServiceRequestUploadBytes is < 1024 or > 1073741824 ||
            string.IsNullOrWhiteSpace(request.DocumentStoragePath) ||
            string.IsNullOrWhiteSpace(request.TimeZone) ||
            string.IsNullOrWhiteSpace(request.DateFormat))
            return Problem(statusCode: 400, title: "Invalid system settings");
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone);
            _ = DateTime.UtcNow.ToString(request.DateFormat, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return Problem(statusCode: 400, title: "Invalid time zone or date format");
        }
        var values = new Dictionary<string, string>
        {
            ["CompanyName"] = request.CompanyName.Trim(),
            ["CompanyLogo"] = Clean(request.CompanyLogo),
            ["DefaultPdfFooter"] = request.DefaultPdfFooter.Trim(),
            ["NotificationDays"] = request.NotificationDays.ToString(),
            ["InspectionDraftWarningDays"] = request.InspectionDraftWarningDays.ToString(),
            ["MaximumDocumentUploadBytes"] = request.MaximumDocumentUploadBytes.ToString(),
            ["MaximumInspectionPhotoBytes"] = request.MaximumInspectionPhotoBytes.ToString(),
            ["MaximumServiceRequestUploadBytes"] = request.MaximumServiceRequestUploadBytes.ToString(),
            ["DocumentStoragePath"] = request.DocumentStoragePath.Trim(),
            ["TimeZone"] = request.TimeZone,
            ["DateFormat"] = request.DateFormat
        };
        var now = DateTime.UtcNow;
        var userId = CurrentUserId();
        var current = await db.SystemSettings
            .Where(x => SettingKeys.Contains(x.Key)).ToDictionaryAsync(x => x.Key, ct);
        foreach (var pair in values)
        {
            if (!current.TryGetValue(pair.Key, out var row))
            {
                row = new SystemSetting { Key = pair.Key };
                db.SystemSettings.Add(row);
            }
            row.Value = pair.Value; row.UpdatedUtc = now; row.UpdatedByUserId = userId;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, "AdministrationSettingsChanged",
            "SystemSettings", null, "System settings updated.", ct);
        return NoContent();
    }

    [HttpGet("backups")]
    public async Task<IActionResult> BackupList(CancellationToken ct) => Ok(
        await db.SystemBackups.AsNoTracking().OrderByDescending(x => x.CreatedUtc)
            .Select(x => new { x.Id, x.BackupType, x.FileName, x.FileSize,
                x.CreatedUtc, x.CreatedByName }).ToListAsync(ct));

    [HttpPost("backups/{type}")]
    public async Task<IActionResult> CreateBackup(string type, CancellationToken ct)
    {
        type = type.Trim();
        if (type is not ("Database" or "Documents" or "Full"))
            return Problem(statusCode: 400, title: "Invalid backup type");
        var id = Guid.NewGuid();
        BackupArtifact artifact;
        try { artifact = await backups.CreateAsync(id, type, ct); }
        catch (FileNotFoundException exception)
        {
            return Problem(statusCode: 409, title: "Backup source unavailable",
                detail: exception.Message);
        }
        var row = new SystemBackup
        {
            Id = id, BackupType = type, FileName = artifact.FileName,
            FileSize = artifact.Size, CreatedUtc = DateTime.UtcNow,
            CreatedByUserId = CurrentUserId(),
            CreatedByName = User.Identity?.Name ?? "Administrator"
        };
        db.Add(row); await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, "BackupCreated", "SystemBackup",
            id.ToString(), type, ct);
        return Ok(new { row.Id, row.BackupType, row.FileName, row.FileSize,
            row.CreatedUtc, row.CreatedByName });
    }

    [HttpPost("backups/{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        if (requests.Count > 1)
            return Problem(statusCode: 409, title: "Restore blocked",
                detail: "Other authenticated requests are active.");
        var row = await db.SystemBackups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Problem(statusCode: 404, title: "Backup not found");
        try { await backups.StageRestoreAsync(row.FileName, ct); }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException)
        {
            return Problem(statusCode: 400, title: "Invalid backup",
                detail: exception.Message);
        }
        await audit.WriteAsync(User, "BackupRestoreScheduled", "SystemBackup",
            id.ToString(), "Backup validated and scheduled for the next startup.", ct);
        return Accepted(value: new
        {
            message = "Backup is valid and will be restored during the next application startup.",
            requiresRestart = true
        });
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var databasePath = backups.DatabasePath;
        var migration = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault()
            ?? "No migration history";
        var metadata = await StoredFiles(ct);
        var physical = Directory.Exists(backups.DocumentRoot)
            ? Directory.EnumerateFiles(backups.DocumentRoot, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var expected = metadata.Select(ResolveStoredPath)
            .Where(x => x is not null).Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        var lastBackup = await db.SystemBackups.AsNoTracking()
            .MaxAsync(x => (DateTime?)x.CreatedUtc, ct);
        var drive = new DriveInfo(Path.GetPathRoot(backups.DocumentRoot)!);
        return Ok(new
        {
            database = new
            {
                status = await db.Database.CanConnectAsync(ct) ? "OK" : "Unavailable",
                size = System.IO.File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0,
                migrationVersion = migration
            },
            documents = new
            {
                storagePath = configuration["Storage:RootPath"] ?? "Documents",
                totalFiles = physical.Count,
                missingFiles = expected.Count(x => !physical.Contains(x)),
                orphanFiles = physical.Count(x => !expected.Contains(x))
            },
            application = new
            {
                version,
                applicationVersion = SystemVersions.ApplicationVersion,
                buildDate = System.IO.File.GetLastWriteTimeUtc(typeof(Program).Assembly.Location),
                databaseVersion = connection.ServerVersion,
                databaseSchemaVersion = lifecycle.CurrentSchemaVersion,
                minimumSupportedDatabaseVersion =
                    SystemVersions.MinimumSupportedDatabaseVersion,
                pendingMigrationCount = lifecycle.PendingMigrationCount,
                databaseLifecycleMode = lifecycle.Mode,
                startupMigrationResult = lifecycle.StartupMigrationResult,
                migrationLockActive = lifecycle.MigrationLockActive,
                lastSuccessfulBackupUtc = lastBackup,
                backupAgeHours = lastBackup is null ? null :
                    (double?)(DateTime.UtcNow - lastBackup.Value).TotalHours,
                storageFreeBytes = drive.AvailableFreeSpace
            }
        });
    }

    [HttpGet("diagnostics")]
    public async Task<IActionResult> Diagnostics(CancellationToken ct)
    {
        var settings = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(
            x => x.Key, x => x.Value, ct);
        return Ok(new
        {
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? "Production",
            storage = new
            {
                configured = configuration["Storage:RootPath"] ?? "Documents",
                exists = Directory.Exists(backups.DocumentRoot),
                writable = IsWritable(backups.DocumentRoot)
            },
            jwt = new
            {
                configured = !string.IsNullOrWhiteSpace(configuration["Jwt:Key"]),
                issuer = configuration["Jwt:Issuer"],
                audience = configuration["Jwt:Audience"]
            },
            notificationWorker = new
            {
                enabled = configuration.GetValue<bool>("Notifications:Enabled"),
                warningDays = Number(settings.GetValueOrDefault(
                    "NotificationDays", "30"), 30)
            },
            pendingNotifications = await PendingNotifications(ct),
            databaseConnection = await db.Database.CanConnectAsync(ct) ? "OK" : "Unavailable",
            pdfEngine = "QuestPDF Community",
            databaseLifecycle = new
            {
                lifecycle.Mode, lifecycle.CurrentSchemaVersion,
                lifecycle.PendingMigrationCount, lifecycle.StartupMigrationResult,
                lifecycle.MigrationLockActive,
                applicationVersion = SystemVersions.ApplicationVersion,
                minimumSupportedDatabaseVersion =
                    SystemVersions.MinimumSupportedDatabaseVersion
            }
        });
    }

    [HttpGet("audit")]
    public async Task<IActionResult> AuditLog(int page = 1, int pageSize = 100,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, 500);
        var query = db.SystemAuditEntries.AsNoTracking().OrderByDescending(x => x.CreatedUtc);
        return Ok(new
        {
            total = await query.CountAsync(ct), page, pageSize,
            items = await query.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new { x.Id, x.CreatedUtc, x.UserName, x.Action,
                    x.ObjectType, x.ObjectId, x.Details }).ToListAsync(ct)
        });
    }

    [HttpPost("maintenance/{operation}")]
    public async Task<IActionResult> Maintenance(string operation, CancellationToken ct)
    {
        string details;
        switch (operation)
        {
            case "integrity-check":
                await db.Database.OpenConnectionAsync(ct);
                await using (var command = db.Database.GetDbConnection().CreateCommand())
                {
                    command.CommandText = "PRAGMA integrity_check;";
                    details = Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? "unknown";
                }
                await db.Database.CloseConnectionAsync();
                break;
            case "rebuild-indexes":
                await db.Database.ExecuteSqlRawAsync("REINDEX;", ct);
                details = "Indexes rebuilt.";
                break;
            case "vacuum":
                await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
                details = "Database vacuum completed.";
                break;
            case "cleanup-orphan-files":
                details = $"{await CleanupOrphanFiles(ct)} orphan files removed.";
                break;
            case "cleanup-orphan-metadata":
                details = $"{await CleanupOrphanMetadata(ct)} orphan metadata records removed.";
                break;
            default:
                return Problem(statusCode: 400, title: "Invalid maintenance operation");
        }
        await audit.WriteAsync(User, "DatabaseMaintenance", "System", operation, details, ct);
        return Ok(new { operation, details });
    }

    [HttpGet("exports/{name}.{format}")]
    public async Task<IActionResult> Export(string name, string format, CancellationToken ct)
    {
        if (format is not ("csv" or "xlsx"))
            return Problem(statusCode: 400, title: "Invalid export format");
        var rows = await ExportRows(name, ct);
        if (rows is null) return Problem(statusCode: 400, title: "Invalid export");
        var fileName = $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{format}";
        if (format == "csv")
        {
            var csv = string.Join(Environment.NewLine,
                rows.Select(row => string.Join(",", row.Select(Csv))));
            return File(Encoding.UTF8.GetPreamble().Concat(
                Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", fileName);
        }
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Export");
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++)
                sheet.Cell(r + 1, c + 1).Value = rows[r][c];
        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    private async Task<List<List<string>>?> ExportRows(string name, CancellationToken ct)
    {
        if (name == "audit")
        {
            var items = await db.SystemAuditEntries.AsNoTracking()
                .OrderByDescending(x => x.CreatedUtc).ToListAsync(ct);
            return [["Date", "User", "Action", "Object", "Details"], .. items.Select(x =>
                new List<string> { x.CreatedUtc.ToString("O"), x.UserName, x.Action,
                    $"{x.ObjectType} {x.ObjectId}".Trim(), x.Details ?? "" })];
        }
        if (name == "settings")
        {
            var items = await db.SystemSettings.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct);
            return [["Setting", "Value", "Updated"], .. items.Select(x =>
                new List<string> { x.Key, x.Value, x.UpdatedUtc.ToString("O") })];
        }
        if (name is "fleet-summary" or "dashboard-statistics")
        {
            var items = await db.Vessels.AsNoTracking().OrderBy(x => x.VesselName)
                .Select(x => new
                {
                    x.VesselName, x.ImoNumber, Equipment = x.Equipment.Count,
                    Inspections = x.InspectionReports.Count,
                    Services = x.ServiceRequests.Count
                }).ToListAsync(ct);
            return [["Vessel", "IMO", "Equipment", "Inspections", "Service Requests"],
                .. items.Select(x => new List<string> { x.VesselName, x.ImoNumber,
                    x.Equipment.ToString(), x.Inspections.ToString(), x.Services.ToString() })];
        }
        return null;
    }

    private async Task<List<string>> StoredFiles(CancellationToken ct)
    {
        var result = await db.EquipmentDocuments.AsNoTracking()
            .Select(x => x.StoredFileName).ToListAsync(ct);
        result.AddRange(await db.InspectionPhotos.AsNoTracking()
            .Select(x => x.StoredFileName).ToListAsync(ct));
        result.AddRange(await db.InspectionReportPdfs.AsNoTracking()
            .Select(x => x.StoredFileName).ToListAsync(ct));
        result.AddRange(await db.ServiceRequestAttachments.AsNoTracking()
            .Select(x => x.StoredFileName).ToListAsync(ct));
        result.AddRange(await db.ServiceRequestPdfs.AsNoTracking()
            .Select(x => x.StoredFileName).ToListAsync(ct));
        return result;
    }

    private string? ResolveStoredPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.Split('/', '\\').Any(x => x == "..")) return null;
        var path = Path.GetFullPath(Path.Combine(backups.DocumentRoot,
            value.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(backups.DocumentRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private async Task<int> CleanupOrphanFiles(CancellationToken ct)
    {
        if (!Directory.Exists(backups.DocumentRoot)) return 0;
        var expected = (await StoredFiles(ct)).Select(ResolveStoredPath)
            .Where(x => x is not null).Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(
            backups.DocumentRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!expected.Contains(Path.GetFullPath(file)))
            {
                System.IO.File.Delete(file); removed++;
            }
        }
        return removed;
    }

    private async Task<int> CleanupOrphanMetadata(CancellationToken ct)
    {
        var missingDocuments = (await StoredFiles(ct)).Where(x =>
        {
            var path = ResolveStoredPath(x);
            return path is null || !System.IO.File.Exists(path);
        }).ToHashSet();
        var removed = await db.EquipmentDocuments
            .Where(x => missingDocuments.Contains(x.StoredFileName)).ExecuteDeleteAsync(ct);
        removed += await db.InspectionPhotos
            .Where(x => missingDocuments.Contains(x.StoredFileName)).ExecuteDeleteAsync(ct);
        removed += await db.InspectionReportPdfs
            .Where(x => missingDocuments.Contains(x.StoredFileName)).ExecuteDeleteAsync(ct);
        removed += await db.ServiceRequestAttachments
            .Where(x => missingDocuments.Contains(x.StoredFileName)).ExecuteDeleteAsync(ct);
        removed += await db.ServiceRequestPdfs
            .Where(x => missingDocuments.Contains(x.StoredFileName)).ExecuteDeleteAsync(ct);
        return removed;
    }

    private async Task<int> PendingNotifications(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var due = today.AddDays(30);
        return await db.Equipment.CountAsync(x => x.ExpiryDate != null &&
            x.ExpiryDate <= due, ct);
    }
    private Guid CurrentUserId() => Guid.Parse(
        User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static int Number(string value, int fallback) =>
        int.TryParse(value, out var result) ? result : fallback;
    private static long Long(string value, long fallback) =>
        long.TryParse(value, out var result) ? result : fallback;
    private static string Clean(string? value) => value?.Trim() ?? "";
    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";
    private static bool IsWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var file = Path.Combine(path, $".write-{Guid.NewGuid():N}");
            System.IO.File.WriteAllText(file, "");
            System.IO.File.Delete(file);
            return true;
        }
        catch { return false; }
    }
}
