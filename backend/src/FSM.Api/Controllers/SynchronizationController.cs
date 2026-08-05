using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FSM.Api.Authorization;
using FSM.Api.Synchronization;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Controllers;

[ApiController]
[Route("api/synchronization")]
public sealed class SynchronizationController(
    FsmDbContext db,
    SynchronizationTokenService tokens,
    SynchronizationStatusService statuses,
    SynchronizationClient client,
    SynchronizationExchangeService exchange,
    SynchronizationFileService files,
    ShipConfigurationProtectionService configurationProtection,
    VesselAccessService vesselAccess,
    IOptions<SynchronizationOptions> options,
    IConfiguration configuration) : ControllerBase
{
    private readonly SynchronizationOptions options = options.Value;
    private readonly string edition =
        configuration["Application:Edition"] ?? "Office";

    [AllowAnonymous]
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var databaseAvailable = false;
        try
        {
            databaseAvailable = await db.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            // Health reports availability without exposing connection details.
        }
        return Ok(new
        {
            applicationEdition = edition,
            applicationVersion = options.ApplicationVersion,
            serverUtcTime = DateTime.UtcNow,
            databaseAvailable,
            synchronizationAvailable = databaseAvailable &&
                edition.Equals("Office", StringComparison.OrdinalIgnoreCase)
        });
    }

    [Authorize]
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        CancellationToken cancellationToken)
    {
        var vessels = await vesselAccess.GetAccessibleVesselsAsync(
            User,
            cancellationToken);
        if (vessels is null)
            return Unauthorized();
        return Ok(new
        {
            edition,
            applicationVersion = options.ApplicationVersion,
            vessels = await statuses.GetAsync(vessels, cancellationToken)
        });
    }

    [Authorize]
    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs(
        Guid? vesselId,
        CancellationToken cancellationToken)
    {
        var vesselIds = await vesselAccess.GetAccessibleVesselIdsAsync(
            User,
            cancellationToken);
        if (vesselIds is null)
            return Unauthorized();
        if (vesselId.HasValue && !vesselIds.Contains(vesselId.Value))
            return Forbid();

        var query = db.SynchronizationLogs
            .AsNoTracking()
            .Where(x => vesselIds.Contains(x.VesselId));
        if (vesselId.HasValue)
            query = query.Where(x => x.VesselId == vesselId.Value);

        var logs = await query
            .OrderByDescending(x => x.DateUtc)
            .Take(100)
            .Select(x => new
            {
                x.Id,
                x.DateUtc,
                x.VesselId,
                Vessel = x.Vessel!.VesselName,
                x.Direction,
                durationSeconds = Math.Round(
                    x.DurationMilliseconds / 1000d,
                    1),
                x.UploadedItems,
                x.DownloadedItems,
                x.Result,
                x.ErrorMessage,
                x.EndpointHost,
                x.HttpStatus,
                x.Phase,
                x.RejectedItems
            })
            .ToListAsync(cancellationToken);
        return Ok(logs);
    }

    [Authorize]
    [HttpPost("run/{vesselId:guid}")]
    public async Task<IActionResult> Run(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        if (!await vesselAccess.CanAccessVesselAsync(
            User,
            vesselId,
            cancellationToken))
            return Forbid();

        try
        {
            return Ok(await client.RunAsync(vesselId, cancellationToken));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            HttpRequestException or
            InvalidDataException)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { message = exception.Message });
        }
    }

    [Authorize]
    [HttpPut("settings/{vesselId:guid}")]
    public async Task<IActionResult> UpdateSettings(
        Guid vesselId,
        UpdateSynchronizationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (edition.Equals("Office", StringComparison.OrdinalIgnoreCase) &&
            !User.IsInRole("OfficeAdmin"))
            return Forbid();
        if (!await vesselAccess.CanAccessVesselAsync(User, vesselId, cancellationToken))
            return Forbid();
        if (request.AutomaticIntervalHours is not (0 or 6 or 12 or 24))
        {
            return BadRequest(new
            {
                message = "Interval must be Manual only, 6, 12 or 24 hours."
            });
        }

        var remoteUrl = request.RemoteBaseUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(remoteUrl) &&
            (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ||
             (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
              !(request.AllowInsecureHttp &&
                uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))))
        {
            return BadRequest(new
            {
                message = "The synchronization URL must use HTTPS unless local HTTP test mode is explicitly enabled."
            });
        }

        try
        {
            var setting = await tokens.GetOrCreateSettingAsync(
                vesselId,
                cancellationToken);
            var automaticWasDisabled = setting.AutomaticIntervalHours == 0;
            setting.AutomaticIntervalHours = request.AutomaticIntervalHours;
            setting.RemoteBaseUrl = string.IsNullOrWhiteSpace(remoteUrl)
                ? null
                : remoteUrl.TrimEnd('/');
            setting.Enabled = request.Enabled;
            setting.AllowInsecureHttp = request.AllowInsecureHttp;
            if (automaticWasDisabled && request.AutomaticIntervalHours > 0)
                setting.LastAttemptUtc = null;
            setting.LastModifiedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [Authorize(Roles = "OfficeAdmin")]
    [HttpPost("token/{vesselId:guid}")]
    public async Task<IActionResult> RotateToken(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = await tokens.RotateAsync(vesselId, cancellationToken);
            var setting = await tokens.GetOrCreateSettingAsync(vesselId, cancellationToken);
            setting.Enabled = true;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(new
            {
                token,
                message =
                    "Copy this token to the vessel configuration now. " +
                    "It will not be displayed again."
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [Authorize(Roles = "OfficeAdmin")]
    [HttpPost("configuration/export/{vesselId:guid}")]
    public async Task<IActionResult> ExportConfiguration(
        Guid vesselId,
        ExportShipConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (!edition.Equals("Office", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Ship configuration can only be exported by Office Edition." });
        try
        {
            var vessel = await db.Vessels.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == vesselId, cancellationToken);
            if (vessel is null)
                return NotFound();
            var setting = await tokens.GetOrCreateSettingAsync(vesselId, cancellationToken);
            if (!setting.Enabled || string.IsNullOrWhiteSpace(setting.RemoteBaseUrl))
                return BadRequest(new { message = "Enable synchronization and configure the effective Office URL first." });
            var payload = new ShipSynchronizationConfiguration(
                setting.RemoteBaseUrl,
                vessel.Id,
                vessel.ImoNumber,
                tokens.GetOutboundToken(setting),
                options.ApplicationVersion,
                setting.AutomaticIntervalHours,
                setting.AllowInsecureHttp);
            var protectedConfiguration = configurationProtection.Protect(payload, request.Passphrase);
            return File(
                Encoding.UTF8.GetBytes(protectedConfiguration),
                "application/json",
                $"FSM-Ship-Sync-{vessel.ImoNumber}.json");
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [Authorize]
    [HttpPost("configuration/import")]
    public async Task<IActionResult> ImportConfiguration(
        ImportShipConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (!edition.Equals("Ship", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Ship configuration can only be imported by Ship Edition." });
        try
        {
            var imported = configurationProtection.Unprotect(
                request.ProtectedConfiguration,
                request.Passphrase);
            return await SaveShipConfigurationAsync(imported, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [Authorize]
    [HttpPut("configuration/manual")]
    public async Task<IActionResult> SaveManualShipConfiguration(
        ManualShipConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (!edition.Equals("Ship", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Manual remote configuration is only available in Ship Edition." });
        return await SaveShipConfigurationAsync(
            new ShipSynchronizationConfiguration(
                request.OfficeEndpointUrl,
                request.VesselId,
                request.VesselImo,
                request.SynchronizationToken,
                options.ApplicationVersion,
                request.AutomaticIntervalHours,
                request.AllowInsecureHttp),
            cancellationToken);
    }

    [Authorize]
    [HttpPost("test/{vesselId:guid}")]
    public async Task<IActionResult> TestConnection(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        if (!await vesselAccess.CanAccessVesselAsync(User, vesselId, cancellationToken))
            return Forbid();
        try
        {
            var response = await client.TestConnectionAsync(vesselId, cancellationToken);
            return Ok(new
            {
                connected = true,
                response.ApplicationVersion,
                response.Edition,
                response.VesselId,
                response.VesselImo,
                response.ServerTimeUtc
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = SafeError(exception) });
        }
    }

    [Authorize(Roles = "OfficeAdmin")]
    [HttpGet("diagnostics")]
    public async Task<IActionResult> ExportDiagnostics(CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var settings = await db.SynchronizationSettings.AsNoTracking()
                .Select(x => new
                {
                    x.VesselId,
                    Vessel = x.Vessel!.VesselName,
                    Imo = x.Vessel.ImoNumber,
                    x.Enabled,
                    x.RemoteBaseUrl,
                    x.AllowInsecureHttp,
                    x.AutomaticIntervalHours,
                    x.LastAttemptUtc,
                    x.LastSynchronizationUtc,
                    x.LastStatus,
                    x.LastError
                }).ToListAsync(cancellationToken);
            await WriteZipEntryAsync(archive, "sanitized-settings.json", JsonSerializer.Serialize(new
            {
                applicationVersion = options.ApplicationVersion,
                edition,
                generatedUtc = DateTime.UtcNow,
                settings
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            var recentLogs = await db.SynchronizationLogs.AsNoTracking()
                .OrderByDescending(x => x.DateUtc).Take(200)
                .Select(x => new { x.DateUtc, x.VesselId, x.Direction, x.EndpointHost, x.HttpStatus, x.Phase, x.UploadedItems, x.DownloadedItems, x.RejectedItems, x.Result, x.ErrorMessage })
                .ToListAsync(cancellationToken);
            await WriteZipEntryAsync(archive, "recent-sync-logs.json", JsonSerializer.Serialize(recentLogs, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            await WriteZipEntryAsync(archive, "endpoint-health.json", JsonSerializer.Serialize(new
            {
                applicationEdition = edition,
                applicationVersion = options.ApplicationVersion,
                serverUtcTime = DateTime.UtcNow,
                databaseAvailable = await db.Database.CanConnectAsync(cancellationToken),
                synchronizationAvailable = edition.Equals("Office", StringComparison.OrdinalIgnoreCase)
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            var integrity = await GetIntegrityCheckAsync(cancellationToken);
            await WriteZipEntryAsync(archive, "database-integrity.txt", integrity, cancellationToken);
            await WriteZipEntryAsync(archive, "firewall-status.txt", await GetFirewallStatusAsync(cancellationToken), cancellationToken);
        }
        return File(output.ToArray(), "application/zip", $"FSM-Sync-Diagnostics-{DateTime.UtcNow:yyyyMMdd}.zip");
    }

    [AllowAnonymous]
    [HttpGet("handshake")]
    public async Task<IActionResult> Handshake(
        Guid vesselId,
        string vesselImo,
        string version,
        CancellationToken cancellationToken)
    {
        var validation = await ValidatePeerAsync(
            vesselId,
            version,
            cancellationToken);
        if (validation is not null)
            return validation;

        var vessel = await db.Vessels.AsNoTracking().SingleAsync(
            x => x.Id == vesselId, cancellationToken);
        if (!string.Equals(vessel.ImoNumber, vesselImo?.Trim(),
            StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "The configured vessel IMO does not match the Office vessel."
            });

        return Ok(new SynchronizationHandshakeResponse(
            options.ApplicationVersion,
            edition,
            vesselId,
            vessel.ImoNumber,
            DateTime.UtcNow));
    }

    [AllowAnonymous]
    [HttpPost("exchange")]
    public async Task<IActionResult> Exchange(
        SynchronizationExchangeRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await ValidatePeerAsync(
            request.VesselId,
            request.ApplicationVersion,
            cancellationToken);
        if (validation is not null)
            return validation;

        var stopwatch = Stopwatch.StartNew();
        var setting = await tokens.GetOrCreateSettingAsync(
            request.VesselId,
            cancellationToken);
        setting.CurrentStage = "Uploading changes...";
        setting.LastAttemptUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var applied = await exchange.ApplyAsync(
                request.Changes,
                request.VesselId,
                request.RemoteCursorUtc,
                request.SourceEdition,
                cancellationToken);
            setting.CurrentStage = "Downloading updates...";
            await db.SaveChangesAsync(cancellationToken);
            var snapshotUtc = DateTime.UtcNow;
            var outgoing = await exchange.BuildPayloadAsync(
                request.VesselId,
                request.RemoteCursorUtc,
                snapshotUtc,
                cancellationToken);
            var result = applied.Conflicts == 0 ? "Success" : "Warning";
            var message = applied.Conflicts == 0
                ? null
                : $"{applied.Conflicts} newer Office item(s) were retained.";

            setting.LastUploadedUtc = snapshotUtc;
            setting.LastSynchronizationUtc = DateTime.UtcNow;
            setting.LastStatus = result;
            setting.LastError = message;
            setting.CurrentStage = "Synchronization completed.";
            db.SynchronizationLogs.Add(new SynchronizationLog
            {
                Id = Guid.NewGuid(),
                VesselId = request.VesselId,
                DateUtc = DateTime.UtcNow,
                Direction = $"{request.SourceEdition} ↔ {edition}",
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                UploadedItems = applied.Accepted,
                DownloadedItems = outgoing.RecordCount + outgoing.Files.Count,
                Result = result,
                ErrorMessage = message,
                EndpointHost = Request.Host.Host,
                HttpStatus = StatusCodes.Status200OK,
                Phase = setting.CurrentStage,
                RejectedItems = applied.Conflicts
            });
            await db.SaveChangesAsync(cancellationToken);

            return Ok(new SynchronizationExchangeResponse(
                options.ApplicationVersion,
                edition,
                request.VesselId,
                snapshotUtc,
                outgoing,
                applied.Accepted,
                applied.Conflicts,
                applied.Warnings));
        }
        catch (Exception exception)
        {
            setting.LastStatus = "Failed";
            setting.LastError = exception.Message;
            setting.CurrentStage = "Failed";
            db.SynchronizationLogs.Add(new SynchronizationLog
            {
                Id = Guid.NewGuid(),
                VesselId = request.VesselId,
                DateUtc = DateTime.UtcNow,
                Direction = $"{request.SourceEdition} → {edition}",
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                Result = "Failed",
                ErrorMessage = SafeError(exception),
                EndpointHost = Request.Host.Host,
                HttpStatus = StatusCodes.Status500InternalServerError,
                Phase = setting.CurrentStage
            });
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    [AllowAnonymous]
    [HttpPost("file-transfers/prepare")]
    public async Task<IActionResult> PrepareFile(
        PrepareSynchronizationFileRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await ValidatePeerAsync(
            request.VesselId,
            request.ApplicationVersion,
            cancellationToken);
        if (validation is not null)
            return validation;
        var vessel = await db.Vessels.SingleAsync(
            x => x.Id == request.VesselId,
            cancellationToken);
        try
        {
            return Ok(await files.PrepareAsync(
                vessel,
                request.File,
                cancellationToken));
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [AllowAnonymous]
    [RequestSizeLimit(2L * 1024L * 1024L)]
    [HttpPut("file-transfers/{transferId:guid}/chunks")]
    public async Task<IActionResult> UploadChunk(
        Guid transferId,
        Guid vesselId,
        long offset,
        string version,
        CancellationToken cancellationToken)
    {
        var validation = await ValidatePeerAsync(
            vesselId,
            version,
            cancellationToken);
        if (validation is not null)
            return validation;
        var transfer = await db.SynchronizationTransfers
            .SingleOrDefaultAsync(
                x => x.Id == transferId && x.VesselId == vesselId,
                cancellationToken);
        if (transfer is null)
            return NotFound();
        try
        {
            var nextOffset = await files.AppendAsync(
                transfer,
                offset,
                Request.Body,
                cancellationToken);
            return Ok(new { nextOffset });
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [AllowAnonymous]
    [HttpPost("file-transfers/{transferId:guid}/complete")]
    public async Task<IActionResult> CompleteFile(
        Guid transferId,
        CompleteSynchronizationFileRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await ValidatePeerAsync(
            request.VesselId,
            request.ApplicationVersion,
            cancellationToken);
        if (validation is not null)
            return validation;
        var transfer = await db.SynchronizationTransfers
            .SingleOrDefaultAsync(
                x => x.Id == transferId &&
                     x.VesselId == request.VesselId,
                cancellationToken);
        if (transfer is null)
            return NotFound();
        var vessel = await db.Vessels.SingleAsync(
            x => x.Id == request.VesselId,
            cancellationToken);
        try
        {
            await files.CompleteAsync(
                vessel,
                transfer,
                cancellationToken);
            return NoContent();
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [AllowAnonymous]
    [HttpGet("files/chunk")]
    public async Task<IActionResult> DownloadChunk(
        Guid vesselId,
        string path,
        long size,
        string checksum,
        DateTime lastModifiedUtc,
        long offset,
        string version,
        CancellationToken cancellationToken)
    {
        var validation = await ValidatePeerAsync(
            vesselId,
            version,
            cancellationToken);
        if (validation is not null)
            return validation;
        var vessel = await db.Vessels.SingleAsync(
            x => x.Id == vesselId,
            cancellationToken);
        try
        {
            var content = await files.ReadChunkAsync(
                vessel,
                new SynchronizationFileItem(
                    path,
                    size,
                    checksum,
                    lastModifiedUtc),
                offset,
                cancellationToken);
            return File(content, "application/octet-stream");
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private async Task<IActionResult?> ValidatePeerAsync(
        Guid vesselId,
        string version,
        CancellationToken cancellationToken)
    {
        var setting = await db.SynchronizationSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.VesselId == vesselId, cancellationToken);
        var allowHttp = options.AllowInsecureHttp ||
            (setting?.AllowInsecureHttp ?? false);
        if (!Request.IsHttps && !allowHttp)
        {
            return StatusCode(
                StatusCodes.Status426UpgradeRequired,
                new { message = "Synchronization requires HTTPS." });
        }
        if (setting is null || !setting.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "Synchronization is not enabled for this vessel."
            });
        if (!string.Equals(
            version,
            options.ApplicationVersion,
            StringComparison.Ordinal))
        {
            return Conflict(new
            {
                message =
                    $"Application version mismatch. Expected " +
                    $"{options.ApplicationVersion}."
            });
        }

        var token = Request.Headers[
            SynchronizationTokenService.HeaderName].FirstOrDefault();
        if (!await tokens.ValidateAsync(
            vesselId,
            token,
            cancellationToken))
        {
            return Unauthorized(new
            {
                message = "The vessel synchronization token is invalid."
            });
        }

        var vesselExists = await db.Vessels
            .AnyAsync(x => x.Id == vesselId, cancellationToken);
        return vesselExists ? null : NotFound();
    }

    private async Task<IActionResult> SaveShipConfigurationAsync(
        ShipSynchronizationConfiguration imported,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(imported.RequiredApplicationVersion,
            options.ApplicationVersion, StringComparison.Ordinal))
            return Conflict(new { message = $"Application version mismatch. Expected {options.ApplicationVersion}." });
        if (imported.AutomaticIntervalHours is not (0 or 6 or 12 or 24))
            return BadRequest(new { message = "Interval must be Manual only, 6, 12 or 24 hours." });
        var configuredShipId = configuration["Application:ShipVesselId"];
        if (!Guid.TryParse(configuredShipId, out var localVesselId) ||
            localVesselId != imported.VesselId)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "The configuration belongs to a different vessel than this Ship installation."
            });
        var vessel = await db.Vessels.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == localVesselId, cancellationToken);
        if (vessel is null || !string.Equals(vessel.ImoNumber,
            imported.VesselImo?.Trim(), StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "The configuration vessel ID or IMO does not match this Ship installation."
            });
        if (!Uri.TryCreate(imported.OfficeEndpointUrl?.Trim(), UriKind.Absolute, out var endpoint) ||
            (!endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(imported.AllowInsecureHttp && endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))))
            return BadRequest(new
            {
                message = "The Office endpoint must use HTTPS unless local HTTP test mode is explicitly enabled."
            });
        var setting = await tokens.GetOrCreateSettingAsync(localVesselId, cancellationToken);
        setting.RemoteBaseUrl = endpoint.ToString().TrimEnd('/');
        setting.AutomaticIntervalHours = imported.AutomaticIntervalHours;
        setting.AllowInsecureHttp = imported.AllowInsecureHttp;
        setting.Enabled = true;
        setting.LastError = null;
        setting.CurrentStage = "Configuration saved.";
        tokens.SetOutboundToken(setting, imported.SynchronizationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            vesselId = vessel.Id,
            vessel = vessel.VesselName,
            vesselImo = vessel.ImoNumber,
            officeEndpointUrl = setting.RemoteBaseUrl,
            setting.AutomaticIntervalHours,
            setting.AllowInsecureHttp
        });
    }

    private static string SafeError(Exception exception)
    {
        var message = exception.Message;
        try
        {
            using var json = JsonDocument.Parse(message);
            if (json.RootElement.TryGetProperty("message", out var property))
                return property.GetString() ?? message;
        }
        catch (JsonException)
        {
        }
        return message;
    }

    private static async Task WriteZipEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private async Task<string> GetIntegrityCheckAsync(
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "unknown";
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string> GetFirewallStatusAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return "Windows Firewall status is not applicable on this operating system.";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "advfirewall firewall show rule name=\"Fleet Safety Manager Office Synchronization\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to query Windows Firewall.");
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch (Exception exception)
        {
            return $"Windows Firewall query failed safely: {exception.Message}";
        }
    }

}
