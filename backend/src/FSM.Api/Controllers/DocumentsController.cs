using FSM.Api.Models;
using FSM.Api.Services;
using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/equipment/{equipmentId:guid}/documents")]
public sealed class DocumentsController(
    FsmDbContext db,
    DocumentStorageService storage,
    EquipmentDocumentAccessService access) : ControllerBase
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
            return EquipmentNotFound();

        if (!await access.CanAccessVesselAsync(
            User,
            equipment.VesselId,
            cancellationToken))
        {
            return AccessDenied();
        }

        var documents = await db.EquipmentDocuments
            .AsNoTracking()
            .Where(x => x.EquipmentId == equipmentId)
            .OrderBy(x => x.DocumentType)
            .ThenByDescending(x => x.IsCurrent)
            .ThenByDescending(x => x.CreatedUtc)
            .Select(x => new EquipmentDocumentListItemDto(
                x.Id,
                x.DocumentType.ToString(),
                x.FileName,
                x.IssueDate,
                x.ExpiryDate,
                x.DocumentNumber,
                x.Issuer,
                x.Remarks,
                x.IsCurrent,
                x.CreatedUtc,
                x.ArchivedUtc))
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
            .SingleOrDefaultAsync(
                x => x.Id == equipmentId,
                cancellationToken);

        if (equipment is null)
            return EquipmentNotFound();

        if (!await access.CanAccessVesselAsync(
            User,
            equipment.VesselId,
            cancellationToken))
        {
            return AccessDenied();
        }

        string newPath;

        try
        {
            newPath = await storage.SavePdfAsync(
                type,
                equipment.Id,
                file,
                cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid document",
                detail: exception.Message);
        }

        var fileName = DocumentStorageService.SanitizeFileName(
            file.FileName);

        if (type == "report")
        {
            equipment.CurrentReportPath = newPath;
            equipment.CurrentReportFileName = fileName;
        }
        else
        {
            equipment.CurrentCertificatePath = newPath;
            equipment.CurrentCertificateFileName = fileName;
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

        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            db.EquipmentDocuments.Add(new EquipmentDocument
            {
                Id = Guid.NewGuid(),
                EquipmentId = equipmentId,
                DocumentType = documentType,
                FileName = fileName,
                StoredFileName = newPath,
                MimeType = "application/pdf",
                FileSize = file.Length,
                ExpiryDate = equipment.ExpiryDate,
                Remarks = equipment.Remarks,
                IsCurrent = true,
                CreatedUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqliteException
            {
                SqliteExtendedErrorCode: 2067
            })
        {
            storage.DeleteIfExists(newPath);
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Document conflict",
                detail:
                    "Another current document of this type was saved concurrently.");
        }
        catch
        {
            storage.DeleteIfExists(newPath);
            throw;
        }

        return Ok(new
        {
            fileName,
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
            return EquipmentNotFound();

        if (!await access.CanAccessVesselAsync(
            User,
            equipment.VesselId,
            cancellationToken))
        {
            return AccessDenied();
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
            return DocumentNotFound();

        string absolutePath;

        try
        {
            absolutePath = storage.GetAbsolutePath(relativePath);
        }
        catch (InvalidOperationException)
        {
            return DocumentNotFound();
        }

        if (!System.IO.File.Exists(absolutePath))
            return DocumentNotFound();

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
            return EquipmentNotFound();

        if (!await access.CanAccessVesselAsync(
            User,
            equipment.VesselId,
            cancellationToken))
        {
            return AccessDenied();
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

    private ObjectResult EquipmentNotFound() => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Equipment not found",
        detail: "The requested equipment does not exist.");

    private ObjectResult DocumentNotFound() => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Document not found",
        detail: "The requested document or stored file does not exist.");

    private ObjectResult AccessDenied() => Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Access denied",
        detail: "The current user cannot access documents for this equipment.");
}
