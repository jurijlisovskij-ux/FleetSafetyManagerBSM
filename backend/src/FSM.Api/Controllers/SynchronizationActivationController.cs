using System.Data;
using System.Net.Http.Json;
using FSM.Api.Synchronization;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Controllers;

[ApiController]
[Route("api/synchronization/activation")]
public sealed class SynchronizationActivationController(
    FsmDbContext db,
    SynchronizationTokenService tokens,
    SynchronizationProvisioningService provisioning,
    OfficeDiscoveryService discovery,
    SynchronizationClient client,
    IHttpClientFactory httpClientFactory,
    IOptions<SynchronizationOptions> options,
    IConfiguration configuration) : ControllerBase
{
    private readonly SynchronizationOptions options = options.Value;
    private readonly string edition =
        configuration["Application:Edition"] ?? "Office";

    [AllowAnonymous]
    [HttpGet("state")]
    public async Task<IActionResult> State(CancellationToken cancellationToken)
    {
        var configured = await db.SynchronizationSettings
            .AsNoTracking()
            .Where(x => x.Enabled && x.RemoteBaseUrl != null &&
                x.ProtectedToken != null)
            .Select(x => new
            {
                x.VesselId,
                vesselName = x.Vessel!.VesselName,
                imoNumber = x.Vessel.ImoNumber,
                x.LastSynchronizationUtc
            })
            .FirstOrDefaultAsync(cancellationToken);
        var legacyVessels = configured is null
            ? await db.Vessels.AsNoTracking()
                .OrderBy(x => x.VesselName)
                .Take(2)
                .Select(x => new
                {
                    VesselId = x.Id,
                    x.VesselName,
                    x.ImoNumber,
                    LastSynchronizationUtc = (DateTime?)null
                })
                .ToListAsync(cancellationToken)
            : [];
        var legacyVessel = legacyVessels.Count == 1
            ? legacyVessels[0]
            : null;
        return Ok(new
        {
            edition,
            requiresActivation = edition.Equals(
                "Ship",
                StringComparison.OrdinalIgnoreCase) &&
                configured is null && legacyVessel is null,
            vessel = configured ?? (object?)legacyVessel
        });
    }

    [AllowAnonymous]
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(CancellationToken cancellationToken)
    {
        if (!IsOffice())
            return NotFound();
        var vessels = await db.Vessels.AsNoTracking()
            .OrderBy(x => x.VesselName)
            .Select(x => new ActivationVesselItem(
                x.Id,
                x.VesselName,
                x.ImoNumber))
            .ToListAsync(cancellationToken);
        return Ok(new OfficeActivationCatalog(
            options.ApplicationVersion,
            vessels));
    }

    [Authorize(Roles = "OfficeAdmin")]
    [HttpPost("code/{vesselId:guid}")]
    public async Task<IActionResult> GenerateCode(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        if (!IsOffice())
            return NotFound();
        try
        {
            var code = await provisioning.RegenerateActivationCodeAsync(
                vesselId,
                cancellationToken);
            return Ok(new
            {
                activationCode = code,
                expiresUtc = DateTime.UtcNow.AddDays(7)
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [AllowAnonymous]
    [HttpPost("connect")]
    public async Task<IActionResult> Connect(
        ConnectActivationRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsOffice())
            return NotFound();
        if (!string.Equals(
            request.ApplicationVersion,
            options.ApplicationVersion,
            StringComparison.Ordinal))
        {
            return Conflict(new
            {
                message = $"Application version mismatch. Expected {options.ApplicationVersion}."
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var setting = await db.SynchronizationSettings
            .Include(x => x.Vessel)
            .SingleOrDefaultAsync(
                x => x.VesselId == request.VesselId,
                cancellationToken);
        var now = DateTime.UtcNow;
        if (setting?.Vessel is null ||
            !SynchronizationProvisioningService.IsActivationCodeValid(
                setting,
                request.ActivationCode,
                now))
        {
            return BadRequest(new
            {
                message = "The activation code is invalid, expired, or has already been used."
            });
        }

        setting.ActivationCodeUsedUtc = now;
        setting.LastModifiedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(new ActivationCredential(
            setting.VesselId,
            setting.Vessel.VesselName,
            setting.Vessel.ImoNumber,
            setting.Vessel.Flag,
            setting.Vessel.CompanyName,
            setting.SynchronizationIdentity,
            tokens.GetOutboundToken(setting),
            24));
    }

    [AllowAnonymous]
    [HttpGet("vessels")]
    public async Task<IActionResult> DiscoverVessels(
        CancellationToken cancellationToken)
    {
        if (!IsShip())
            return NotFound();
        var offices = await discovery.DiscoverAsync(cancellationToken);
        var vessels = offices
            .SelectMany(x => x.Catalog.Vessels)
            .GroupBy(x => x.VesselId)
            .Select(x => x.First())
            .OrderBy(x => x.VesselName)
            .ToList();
        return Ok(new { vessels });
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> Activate(
        ActivateShipRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsShip())
            return NotFound();

        var offices = await discovery.DiscoverAsync(cancellationToken);
        var office = offices.FirstOrDefault(x =>
            x.Catalog.Vessels.Any(vessel => vessel.VesselId == request.VesselId));
        if (office is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "No Internet connection. Synchronization will continue automatically when the connection is restored."
            });
        }

        var http = httpClientFactory.CreateClient("Synchronization");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                $"{office.BaseUrl}/api/synchronization/activation/connect",
                new ConnectActivationRequest(
                    request.VesselId,
                    SynchronizationProvisioningService.NormalizeActivationCode(
                        request.ActivationCode),
                    options.ApplicationVersion),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "No Internet connection. Synchronization will continue automatically when the connection is restored."
            });
        }

        using var activationResponse = response;
        var credential = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ActivationCredential>(
                cancellationToken: cancellationToken)
            : null;
        if (credential is null)
        {
            ActivationFailure? failure = null;
            try
            {
                failure = await response.Content.ReadFromJsonAsync<ActivationFailure>(
                    cancellationToken: cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
            }
            return StatusCode((int)response.StatusCode, new
            {
                message = failure?.Message ?? "Activation failed."
            });
        }

        var vessel = await db.Vessels
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                x => x.Id == credential.VesselId,
                cancellationToken);
        if (vessel is null)
        {
            vessel = new Vessel
            {
                Id = credential.VesselId,
                VesselName = credential.VesselName,
                ImoNumber = credential.ImoNumber
            };
            db.Vessels.Add(vessel);
        }
        vessel.VesselName = credential.VesselName;
        vessel.ImoNumber = credential.ImoNumber;
        vessel.Flag = credential.Flag;
        vessel.CompanyName = credential.CompanyName;
        vessel.IsDeleted = false;
        vessel.LastModifiedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var setting = await tokens.GetOrCreateSettingAsync(
            vessel.Id,
            cancellationToken);
        setting.SynchronizationIdentity = credential.SynchronizationIdentity;
        setting.RemoteBaseUrl = office.BaseUrl;
        setting.AutomaticIntervalHours = 24;
        setting.AllowInsecureHttp = office.BaseUrl.StartsWith(
            "http://",
            StringComparison.OrdinalIgnoreCase);
        setting.Enabled = true;
        setting.LastError = null;
        setting.CurrentStage = "Synchronizing";
        setting.RetryCount = 0;
        setting.NextRetryUtc = null;
        tokens.SetOutboundToken(setting, credential.SynchronizationToken);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await client.RunAsync(vessel.Id, cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidOperationException or InvalidDataException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                activated = true,
                synchronized = false,
                message = "Activation was saved. Synchronization will continue automatically when the connection is restored."
            });
        }

        return Ok(new
        {
            activated = true,
            synchronized = true,
            vesselId = vessel.Id,
            message = "Synchronization completed successfully."
        });
    }

    private bool IsOffice() => edition.Equals(
        "Office",
        StringComparison.OrdinalIgnoreCase);

    private bool IsShip() => edition.Equals(
        "Ship",
        StringComparison.OrdinalIgnoreCase);

    private sealed record ActivationFailure(string Message);
}
