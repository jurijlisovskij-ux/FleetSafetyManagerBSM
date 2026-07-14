using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using FSM.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public enum CopyEquipmentMode
{
    AddOnlyNew,
    ReplaceExisting
}

public sealed record CopyEquipmentRequest(
    Guid SourceVesselId,
    CopyEquipmentMode Mode);

[ApiController]
[Authorize(Roles = "OfficeAdmin")]
[Route("api/vessels/{targetVesselId:guid}/copy-equipment")]
public sealed class CopyEquipmentController(
    FsmDbContext db,
    DocumentStorageService storage) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Copy(
        Guid targetVesselId,
        CopyEquipmentRequest request,
        CancellationToken cancellationToken)
    {
        if (targetVesselId == request.SourceVesselId)
            return BadRequest(new { message = "Source and target vessels must be different." });

        var target = await db.Vessels.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == targetVesselId, cancellationToken);
        var source = await db.Vessels.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.SourceVesselId, cancellationToken);

        if (target is null || source is null)
            return NotFound();

        var sourceItems = await db.Equipment.AsNoTracking()
            .Where(x => x.VesselId == request.SourceVesselId)
            .OrderBy(x => x.Category)
            .ThenBy(x => x.EquipmentName)
            .ToListAsync(cancellationToken);

        if (sourceItems.Count == 0)
            return BadRequest(new { message = "The source vessel has no equipment." });

        var added = 0;
        var skipped = 0;
        var removed = 0;

        if (request.Mode == CopyEquipmentMode.ReplaceExisting)
        {
            var existingItems = await db.Equipment
                .Where(x => x.VesselId == targetVesselId)
                .ToListAsync(cancellationToken);

            removed = existingItems.Count;

            foreach (var item in existingItems)
            {
                storage.DeleteIfExists(item.CurrentReportPath);
                storage.DeleteIfExists(item.CurrentCertificatePath);
            }

            db.Equipment.RemoveRange(existingItems);

            foreach (var item in sourceItems)
            {
                db.Equipment.Add(CreateCopy(targetVesselId, item));
                added++;
            }
        }
        else
        {
            var existingItems = await db.Equipment.AsNoTracking()
                .Where(x => x.VesselId == targetVesselId)
                .ToListAsync(cancellationToken);

            foreach (var item in sourceItems)
            {
                var exists = existingItems.Any(existing =>
                    existing.Category == item.Category &&
                    Normalize(existing.EquipmentName) == Normalize(item.EquipmentName) &&
                    Normalize(existing.Maker) == Normalize(item.Maker) &&
                    Normalize(existing.Model) == Normalize(item.Model) &&
                    existing.InspectionBy == item.InspectionBy);

                if (exists)
                {
                    skipped++;
                    continue;
                }

                db.Equipment.Add(CreateCopy(targetVesselId, item));
                added++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            sourceCount = sourceItems.Count,
            added,
            skipped,
            removed
        });
    }

    private static Equipment CreateCopy(
        Guid targetVesselId,
        Equipment source) =>
        new()
        {
            Id = Guid.NewGuid(),
            VesselId = targetVesselId,
            Category = source.Category,
            EquipmentName = source.EquipmentName,
            Maker = source.Maker,
            Model = source.Model,
            InspectionBy = source.InspectionBy,
            Quantity = 1,
            SerialNumber = null,
            ExpiryDate = null,
            Remarks = null,
            CurrentReportPath = null,
            CurrentReportFileName = null,
            CurrentCertificatePath = null,
            CurrentCertificateFileName = null
        };

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();
}
