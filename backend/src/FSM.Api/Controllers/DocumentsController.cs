using System.Security.Claims;
using FSM.Api.Services;
using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/equipment/{equipmentId:guid}/documents")]
public sealed class DocumentsController(
    FsmDbContext db,
    DocumentStorageService storage) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid equipmentId,
        CancellationToken cancellationToken)
    {
        var equipment = await db.Equipment
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == equipmentId,
                cancellationToken);

        if (equipment is null)
            return NotFound();

        if (!await CanAccessVesselAsync(
            equipment.VesselId,
            cancellationToken))
        {
            return Forbid();
        }

        var documents = await db.EquipmentDocuments
            .AsNoTracking()
            .Where(x => x.EquipmentId == equipmentId)
            .OrderBy(x => x.DocumentType)
            .ThenByDescending(x => x.IsCurrent)
            .ThenByDescending(x => x.CreatedUtc)
            .Select(x => new
            {
                x.Id,
                Type = x.DocumentType.ToString(),
                x.FileName,
                x.IssueDate,
                x.ExpiryDate,
                x.DocumentNumber,
                x.Issuer,
                x.Remarks,
                x.IsCurrent,
                x.CreatedUtc,
                x.ArchivedUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(documents);
    }

    [HttpPost("{type:regex(^(report|certificate)$)}")]
    [RequestSizeLimit(26L * 1024L * 1024L)]
    public async Task<IActionResult> Upload(
        Guid equipmentId,
        string type,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var equipment = await db.Equipment
            .Include(x => x.Vessel)
            .SingleOrDefaultAsync(
                x => x.Id == equipmentId,
                cancellationToken);

        if (equipment?.Vessel is null)
            return NotFound();

        if (!await CanAccessVesselAsync(
            equipment.VesselId,
            cancellationToken))
        {
            return Forbid();
        }

        var oldPath = type == "report"
            ? equipment.CurrentReportPath
            : equipment.CurrentCertificatePath;

        string newPath;

        try
        {
            newPath = await storage.SavePdfAsync(
                type,
                equipment.Vessel.ImoNumber,
                equipment.Id,
                file,
                cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }

        if (type == "report")
        {
            equipment.CurrentReportPath = newPath;
            equipment.CurrentReportFileName =
                Path.GetFileName(file.FileName);
        }
        else
        {
            equipment.CurrentCertificatePath = newPath;
            equipment.CurrentCertificateFileName =
                Path.GetFileName(file.FileName);
        }

        var documentType = type == "report"
            ? EquipmentDocumentType.ServiceReport
            : EquipmentDocumentType.Certificate;
        var now = DateTime.UtcNow;
        var currentDocuments = await db.EquipmentDocuments
            .Where(x =>
                x.EquipmentId == equipmentId &&
                x.DocumentType == documentType &&
                x.IsCurrent)
            .ToListAsync(cancellationToken);

        foreach (var current in currentDocuments)
        {
            current.IsCurrent = false;
            current.ArchivedUtc = now;
        }

        db.EquipmentDocuments.Add(new EquipmentDocument
        {
            Id = Guid.NewGuid(),
            EquipmentId = equipmentId,
            DocumentType = documentType,
            FileName = Path.GetFileName(file.FileName),
            StoredFileName = newPath,
            MimeType = "application/pdf",
            FileSize = file.Length,
            ExpiryDate = equipment.ExpiryDate,
            Remarks = equipment.Remarks,
            IsCurrent = true,
            CreatedUtc = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            storage.DeleteIfExists(newPath);
            throw;
        }

        return Ok(new
        {
            fileName = Path.GetFileName(file.FileName),
            type
        });
    }

    [HttpGet("{type:regex(^(report|certificate)$)}")]
    public async Task<IActionResult> Open(
        Guid equipmentId,
        string type,
        CancellationToken cancellationToken)
    {
        var equipment = await db.Equipment
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == equipmentId,
                cancellationToken);

        if (equipment is null)
            return NotFound();

        if (!await CanAccessVesselAsync(
            equipment.VesselId,
            cancellationToken))
        {
            return Forbid();
        }

        var documentType = type == "report"
            ? EquipmentDocumentType.ServiceReport
            : EquipmentDocumentType.Certificate;
        var currentDocument = await db.EquipmentDocuments
            .AsNoTracking()
            .Where(x =>
                x.EquipmentId == equipmentId &&
                x.DocumentType == documentType &&
                x.IsCurrent)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var relativePath = currentDocument?.StoredFileName ??
            (type == "report"
                ? equipment.CurrentReportPath
                : equipment.CurrentCertificatePath);

        var originalName = currentDocument?.FileName ??
            (type == "report"
                ? equipment.CurrentReportFileName
                : equipment.CurrentCertificateFileName);

        if (string.IsNullOrWhiteSpace(relativePath))
            return NotFound();

        var absolutePath = storage.GetAbsolutePath(relativePath);

        if (!System.IO.File.Exists(absolutePath))
            return NotFound();

        return PhysicalFile(
            absolutePath,
            "application/pdf",
            originalName ?? $"{type}.pdf",
            enableRangeProcessing: true);
    }

    [HttpDelete("{type:regex(^(report|certificate)$)}")]
    public async Task<IActionResult> Delete(
        Guid equipmentId,
        string type,
        CancellationToken cancellationToken)
    {
        var equipment = await db.Equipment
            .SingleOrDefaultAsync(
                x => x.Id == equipmentId,
                cancellationToken);

        if (equipment is null)
            return NotFound();

        if (!await CanAccessVesselAsync(
            equipment.VesselId,
            cancellationToken))
        {
            return Forbid();
        }

        var oldPath = type == "report"
            ? equipment.CurrentReportPath
            : equipment.CurrentCertificatePath;

        if (type == "report")
        {
            equipment.CurrentReportPath = null;
            equipment.CurrentReportFileName = null;
        }
        else
        {
            equipment.CurrentCertificatePath = null;
            equipment.CurrentCertificateFileName = null;
        }

        var documentType = type == "report"
            ? EquipmentDocumentType.ServiceReport
            : EquipmentDocumentType.Certificate;
        var currentDocuments = await db.EquipmentDocuments
            .Where(x =>
                x.EquipmentId == equipmentId &&
                x.DocumentType == documentType &&
                x.IsCurrent)
            .ToListAsync(cancellationToken);
        db.EquipmentDocuments.RemoveRange(currentDocuments);

        await db.SaveChangesAsync(cancellationToken);
        foreach (var path in currentDocuments
            .Select(x => x.StoredFileName)
            .Append(oldPath)
            .Distinct(StringComparer.Ordinal))
        {
            storage.DeleteIfExists(path);
        }

        return NoContent();
    }

    private async Task<bool> CanAccessVesselAsync(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var role = User.FindFirstValue(ClaimTypes.Role);

        if (string.Equals(
            role,
            "OfficeAdmin",
            StringComparison.Ordinal))
        {
            return true;
        }

        var userIdText =
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(userIdText, out var userId))
            return false;

        return await db.UserVessels
            .AsNoTracking()
            .AnyAsync(
                x => x.UserId == userId &&
                     x.VesselId == vesselId,
                cancellationToken);
    }
}
