using System.Diagnostics;
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
    VesselAccessService vesselAccess,
    IOptions<SynchronizationOptions> options,
    IConfiguration configuration) : ControllerBase
{
    private readonly SynchronizationOptions options = options.Value;
    private readonly string edition =
        configuration["Application:Edition"] ?? "Office";

    [Authorize]
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        CancellationToken cancellationToken)
    {
        var vesselIds = await vesselAccess.GetAccessibleVesselIdsAsync(
            User,
            cancellationToken);
        if (vesselIds is null)
            return Unauthorized();
        return Ok(new
        {
            edition,
            applicationVersion = options.ApplicationVersion,
            vessels = await statuses.GetAsync(vesselIds, cancellationToken)
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
                x.ErrorMessage
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

    [Authorize(Roles = "OfficeAdmin")]
    [HttpPut("settings/{vesselId:guid}")]
    public async Task<IActionResult> UpdateSettings(
        Guid vesselId,
        UpdateSynchronizationSettingsRequest request,
        CancellationToken cancellationToken)
    {
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
             !uri.Scheme.Equals(
                 Uri.UriSchemeHttps,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(new
            {
                message = "The remote synchronization URL must use HTTPS."
            });
        }

        try
        {
            var setting = await tokens.GetOrCreateSettingAsync(
                vesselId,
                cancellationToken);
            setting.AutomaticIntervalHours = request.AutomaticIntervalHours;
            setting.RemoteBaseUrl = string.IsNullOrWhiteSpace(remoteUrl)
                ? null
                : remoteUrl.TrimEnd('/');
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

    [AllowAnonymous]
    [HttpGet("handshake")]
    public async Task<IActionResult> Handshake(
        Guid vesselId,
        string version,
        CancellationToken cancellationToken)
    {
        var validation = await ValidatePeerAsync(
            vesselId,
            version,
            cancellationToken);
        if (validation is not null)
            return validation;

        return Ok(new SynchronizationHandshakeResponse(
            options.ApplicationVersion,
            edition,
            vesselId,
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
                ErrorMessage = message
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
                ErrorMessage = exception.Message
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
        if (!Request.IsHttps)
        {
            return StatusCode(
                StatusCodes.Status426UpgradeRequired,
                new { message = "Synchronization requires HTTPS." });
        }
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

}
