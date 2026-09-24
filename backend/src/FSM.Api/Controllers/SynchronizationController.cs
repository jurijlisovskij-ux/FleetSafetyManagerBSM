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

    [Authorize(Roles = "OfficeAdmin")]
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

    [Authorize(Roles = "OfficeAdmin")]
    [HttpGet("advanced")]
    public async Task<IActionResult> AdvancedDiagnostics(
        CancellationToken cancellationToken)
    {
        if (!edition.Equals("Office", StringComparison.OrdinalIgnoreCase))
            return Forbid();
        var queueCounts = await db.SynchronizationTransfers.AsNoTracking()
            .GroupBy(x => x.VesselId)
            .Select(x => new { VesselId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.VesselId, x => x.Count, cancellationToken);
        var settings = await db.SynchronizationSettings.AsNoTracking()
            .OrderBy(x => x.Vessel!.VesselName)
            .Select(x => new
            {
                x.VesselId,
                vessel = x.Vessel!.VesselName,
                endpoint = x.RemoteBaseUrl,
                tokenFingerprint = x.TokenHash == null
                    ? null
                    : x.TokenHash.Substring(0, 12),
                x.RetryCount,
                x.NextRetryUtc,
                x.LastAttemptUtc,
                x.LastSynchronizationUtc,
                x.LastStatus,
                x.LastError
            })
            .ToListAsync(cancellationToken);
        var recentRequests = await db.SynchronizationLogs.AsNoTracking()
            .OrderByDescending(x => x.DateUtc)
            .Take(100)
            .Select(x => new
            {
                x.DateUtc,
                x.VesselId,
                x.EndpointHost,
                x.HttpStatus,
                x.DurationMilliseconds,
                x.Phase,
                x.Result,
                x.ErrorMessage
            })
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            healthChecks = new
            {
                database = await db.Database.CanConnectAsync(cancellationToken)
                    ? "Available"
                    : "Unavailable",
                applicationVersion = options.ApplicationVersion,
                serverUtc = DateTime.UtcNow
            },
            vessels = settings.Select(x => new
            {
                x.VesselId,
                x.vessel,
                x.endpoint,
                token = x.tokenFingerprint is null
                    ? "Not configured"
                    : $"Configured · SHA-256 {x.tokenFingerprint}…",
                uploadQueue = queueCounts.GetValueOrDefault(x.VesselId),
                downloadQueue = 0,
                x.RetryCount,
                x.NextRetryUtc,
                x.LastAttemptUtc,
                x.LastSynchronizationUtc,
                x.LastStatus,
                x.LastError
            }),
            httpRequests = recentRequests
        });
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
        catch (HttpRequestException exception) when (!exception.StatusCode.HasValue)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    message = "No Internet connection. Synchronization will continue automatically when the connection is restored."
                });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            HttpRequestException or
            InvalidDataException)
        {
            var waitingForInternet = await db.SynchronizationSettings
                .AsNoTracking()
                .Where(x => x.VesselId == vesselId)
                .Select(x => x.CurrentStage == "WaitingForInternet")
                .SingleOrDefaultAsync(cancellationToken);
            if (waitingForInternet)
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new
                    {
                        message = "No Internet connection. Synchronization will continue automatically when the connection is restored."
                    });
            }
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new
                {
                    message = exception.Message,
                    retryMessage = "Synchronization will retry automatically."
                });
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
