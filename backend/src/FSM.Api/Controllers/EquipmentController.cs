using FSM.Api.Authorization;
using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace FSM.Api.Controllers;

public sealed record SaveEquipmentRequest(
    EquipmentCategory Category,
    string EquipmentName,
    string? Maker,
    string? Model,
    string? SerialNumber,
    int Quantity,
    InspectionBy InspectionBy,
    string? ExpiryDate,
    string? Responsible,
    string? Remarks);

[ApiController]
[Authorize]
[Route("api/vessels/{vesselId:guid}/equipment")]
public sealed class EquipmentController(
    FsmDbContext db,
    VesselAccessService vesselAccess) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetByVessel(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var access = await CheckAccessAsync(vesselId, cancellationToken);
        if (access is not null) return access;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var items = await db.Equipment.AsNoTracking()
            .Where(x => x.VesselId == vesselId)
            .OrderBy(x => x.Category)
            .ThenBy(x => x.EquipmentName)
            .ThenBy(x => x.Maker)
            .ThenBy(x => x.Model)
            .ToListAsync(cancellationToken);

        return Ok(items.Select(x => ToResponse(x, today)));
    }


    [HttpPost]
    public async Task<IActionResult> Create(
        Guid vesselId,
        SaveEquipmentRequest request,
        CancellationToken cancellationToken)
    {
        var access = await CheckAccessAsync(
            vesselId,
            cancellationToken);

        if (access is not null)
            return access;

        var validationError = Validate(request);

        if (validationError is not null)
        {
            return BadRequest(new
            {
                message = validationError
            });
        }

        var item = new Equipment
        {
            Id = Guid.NewGuid(),
            VesselId = vesselId,
            Category = request.Category,
            EquipmentName =
                request.EquipmentName.Trim(),
            Maker = Clean(request.Maker),
            Model = Clean(request.Model),
            SerialNumber =
                Clean(request.SerialNumber),
            Quantity = request.Quantity,
            InspectionBy =
                request.InspectionBy,
            ExpiryDate = ParseExpiryDate(request.ExpiryDate),
            Responsible = Clean(request.Responsible),
            Remarks =
                request.Remarks?.Trim()
        };

        db.Equipment.Add(item);
        await db.SaveChangesAsync(
            cancellationToken);

        var today =
            DateOnly.FromDateTime(DateTime.UtcNow);

        return Created(
            $"/api/vessels/{vesselId}/equipment/{item.Id}",
            ToResponse(item, today));
    }

    [HttpPut("{equipmentId:guid}")]
    public async Task<IActionResult> Update(
        Guid vesselId,
        Guid equipmentId,
        SaveEquipmentRequest request,
        CancellationToken cancellationToken)
    {
        var access = await CheckAccessAsync(vesselId, cancellationToken);
        if (access is not null) return access;

        var validationError = Validate(request);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        var item = await db.Equipment.SingleOrDefaultAsync(
            x => x.Id == equipmentId && x.VesselId == vesselId,
            cancellationToken);

        if (item is null) return NotFound();

        item.Category = request.Category;
        item.EquipmentName = request.EquipmentName.Trim();
        item.Maker = Clean(request.Maker);
        item.Model = Clean(request.Model);
        item.SerialNumber = Clean(request.SerialNumber);
        item.Quantity = request.Quantity;
        item.InspectionBy = request.InspectionBy;
        item.ExpiryDate = ParseExpiryDate(request.ExpiryDate);
        item.Responsible = Clean(request.Responsible);
        item.Remarks = request.Remarks?.Trim();

        await db.SaveChangesAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(ToResponse(item, today));
    }

    [HttpDelete("{equipmentId:guid}")]
    public async Task<IActionResult> Delete(
        Guid vesselId,
        Guid equipmentId,
        CancellationToken cancellationToken)
    {
        var access = await CheckAccessAsync(vesselId, cancellationToken);
        if (access is not null) return access;

        var item = await db.Equipment.SingleOrDefaultAsync(
            x => x.Id == equipmentId && x.VesselId == vesselId,
            cancellationToken);

        if (item is null) return NotFound();

        db.Equipment.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<IActionResult?> CheckAccessAsync(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var vesselExists = await db.Vessels.AsNoTracking()
            .AnyAsync(x => x.Id == vesselId, cancellationToken);

        if (!vesselExists) return NotFound();

        return await vesselAccess.CanAccessVesselAsync(
            User,
            vesselId,
            cancellationToken)
                ? null
                : Forbid();
    }

    private static string? Validate(SaveEquipmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EquipmentName))
            return "Equipment Name is required.";
        if (request.Quantity < 1)
            return "Quantity must be greater than zero.";
        if (!string.IsNullOrWhiteSpace(request.ExpiryDate) &&
            ParseExpiryDate(request.ExpiryDate) is null)
            return "Enter a valid Expiry Date.";
        return null;
    }

    private static DateOnly? ParseExpiryDate(string? value) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
                ? date
                : null;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static object ToResponse(Equipment x, DateOnly today) => new
    {
        x.Id,
        x.VesselId,
        Category = x.Category == EquipmentCategory.RadioAndNavigation
            ? "Radio & Navigation"
            : x.Category.ToString(),
        x.EquipmentName,
        x.Maker,
        x.Model,
        x.SerialNumber,
        x.Quantity,
        InspectionBy = x.InspectionBy == InspectionBy.ServiceCompany
            ? "Service Company"
            : "Crew",
        x.ExpiryDate,
        x.Responsible,
        Status = x.CalculateStatus(today, 60).ToString(),
        x.Remarks,
        ReportAvailable =
            !string.IsNullOrWhiteSpace(x.CurrentReportPath),
        ReportFileName = x.CurrentReportFileName,
        CertificateAvailable =
            !string.IsNullOrWhiteSpace(x.CurrentCertificatePath),
        CertificateFileName = x.CurrentCertificateFileName
    };
}
