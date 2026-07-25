using FSM.Api.Models;
using FSM.Api.Services;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/documents")]
public sealed class DocumentRepositoryController(
    FsmDbContext db,
    DocumentStorageService storage,
    EquipmentDocumentAccessService access) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetMetadata(
        Guid id,
        CancellationToken cancellationToken)
    {
        var document = await db.EquipmentDocuments
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                Metadata = new EquipmentDocumentMetadataDto(
                    x.Id,
                    x.EquipmentId,
                    x.DocumentType.ToString(),
                    x.FileName,
                    x.MimeType,
                    x.FileSize,
                    x.IssueDate,
                    x.ExpiryDate,
                    x.DocumentNumber,
                    x.Issuer,
                    x.Remarks,
                    x.IsCurrent,
                    x.CreatedUtc,
                    x.ArchivedUtc),
                x.Equipment!.VesselId
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (document is null)
            return DocumentNotFound();

        if (!await access.CanAccessVesselAsync(
            User,
            document.VesselId,
            cancellationToken))
        {
            return AccessDenied();
        }

        return Ok(document.Metadata);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(
        Guid id,
        CancellationToken cancellationToken)
    {
        var document = await db.EquipmentDocuments
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.StoredFileName,
                x.MimeType,
                x.FileName,
                x.Equipment!.VesselId
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (document is null)
            return DocumentNotFound();

        if (!await access.CanAccessVesselAsync(
            User,
            document.VesselId,
            cancellationToken))
        {
            return AccessDenied();
        }

        string absolutePath;

        try
        {
            absolutePath = storage.GetAbsolutePath(
                document.StoredFileName);
        }
        catch (InvalidOperationException)
        {
            return DocumentNotFound();
        }

        if (!System.IO.File.Exists(absolutePath))
            return DocumentNotFound();

        return PhysicalFile(
            absolutePath,
            document.MimeType,
            document.FileName,
            enableRangeProcessing: true);
    }

    private ObjectResult DocumentNotFound() => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Document not found",
        detail: "The requested document or stored file does not exist.");

    private ObjectResult AccessDenied() => Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Access denied",
        detail: "The current user cannot access this document.");
}
