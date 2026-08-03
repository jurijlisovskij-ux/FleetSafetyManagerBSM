using FSM.Api.Authorization;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public sealed record SaveVesselRequest(
    string VesselName,
    string ImoNumber,
    string? Flag,
    string? CompanyName,
    string? InvoicingDetails,
    string? AccountingEmail);

[ApiController]
[Authorize]
[Route("api/vessels")]
public sealed class VesselsController(
    FsmDbContext db,
    VesselAccessService vesselAccess) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAssigned(
        CancellationToken cancellationToken)
    {
        var vessels = await vesselAccess.GetAccessibleVesselsAsync(
            User,
            cancellationToken);
        if (vessels is null)
            return Unauthorized();

        var response = vessels
            .Select(x => new
            {
                x.Id,
                x.VesselName,
                x.ImoNumber,
                x.Flag,
                x.CompanyName,
                x.InvoicingDetails,
                x.AccountingEmail,
                DisplayName =
                    $"{x.VesselName} (IMO {x.ImoNumber})"
            })
            .ToList();

        return Ok(response);
    }

    [Authorize(Roles = "OfficeAdmin")]
    [HttpPost]
    public async Task<IActionResult> Create(
        SaveVesselRequest request,
        CancellationToken cancellationToken)
    {
        var vesselName = request.VesselName.Trim();
        var imoNumber = request.ImoNumber.Trim();

        if (string.IsNullOrWhiteSpace(vesselName))
            return BadRequest(
                new { message = "Vessel Name is required." });

        if (imoNumber.Length != 7 ||
            !imoNumber.All(char.IsDigit))
        {
            return BadRequest(
                new { message = "IMO Number must contain 7 digits." });
        }

        if (await db.Vessels.AnyAsync(
            x => x.ImoNumber == imoNumber,
            cancellationToken))
        {
            return Conflict(
                new { message = "A vessel with this IMO Number already exists." });
        }

        var vessel = new Vessel
        {
            Id = Guid.NewGuid(),
            VesselName = vesselName,
            ImoNumber = imoNumber,
            Flag = Clean(request.Flag),
            CompanyName = Clean(request.CompanyName),
            InvoicingDetails =
                request.InvoicingDetails?.Trim(),
            AccountingEmail =
                Clean(request.AccountingEmail)
        };

        db.Vessels.Add(vessel);
        await db.SaveChangesAsync(cancellationToken);

        return Created(
            $"/api/vessels/{vessel.Id}",
            new
            {
                vessel.Id,
                vessel.VesselName,
                vessel.ImoNumber,
                vessel.Flag,
                vessel.CompanyName,
                vessel.InvoicingDetails,
                vessel.AccountingEmail,
                DisplayName =
                    $"{vessel.VesselName} (IMO {vessel.ImoNumber})"
            });
    }

    [Authorize(Roles = "OfficeAdmin")]
    [HttpPut("{vesselId:guid}")]
    public async Task<IActionResult> Update(
        Guid vesselId,
        SaveVesselRequest request,
        CancellationToken cancellationToken)
    {
        var vessel = await db.Vessels
            .SingleOrDefaultAsync(
                x => x.Id == vesselId,
                cancellationToken);

        if (vessel is null)
            return NotFound();

        var vesselName = request.VesselName.Trim();
        var imoNumber = request.ImoNumber.Trim();

        if (string.IsNullOrWhiteSpace(vesselName))
        {
            return BadRequest(new
            {
                message = "Vessel Name is required."
            });
        }

        if (imoNumber.Length != 7 ||
            !imoNumber.All(char.IsDigit))
        {
            return BadRequest(new
            {
                message =
                    "IMO Number must contain 7 digits."
            });
        }

        if (await db.Vessels.AnyAsync(
            x => x.Id != vesselId &&
                 x.ImoNumber == imoNumber,
            cancellationToken))
        {
            return Conflict(new
            {
                message =
                    "A vessel with this IMO Number already exists."
            });
        }

        vessel.VesselName = vesselName;
        vessel.ImoNumber = imoNumber;
        vessel.Flag = Clean(request.Flag);
        vessel.CompanyName =
            Clean(request.CompanyName);
        vessel.InvoicingDetails =
            request.InvoicingDetails?.Trim();
        vessel.AccountingEmail =
            Clean(request.AccountingEmail);

        await db.SaveChangesAsync(
            cancellationToken);

        return Ok(new
        {
            vessel.Id,
            vessel.VesselName,
            vessel.ImoNumber,
            vessel.Flag,
            vessel.CompanyName,
            vessel.InvoicingDetails,
            vessel.AccountingEmail,
            DisplayName =
                $"{vessel.VesselName} " +
                $"(IMO {vessel.ImoNumber})"
        });
    }

    private static string? Clean(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned)
            ? null
            : cleaned;
    }
}
