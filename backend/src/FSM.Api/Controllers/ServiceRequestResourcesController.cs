using System.Security.Claims;
using FSM.Api.Models;
using FSM.Api.Services;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController, Authorize, Route("api/service-request-items")]
public sealed class ServiceRequestItemsController(
    FsmDbContext db, EquipmentDocumentAccessService access) : ControllerBase
{
    [HttpPut("{itemId:guid}")]
    public async Task<IActionResult> Update(Guid itemId,
        UpdateServiceRequestItem input, CancellationToken ct)
    {
        var item = await Load(itemId, ct); var denied = await Editable(item, ct);
        if (denied is not null) return denied;
        if (string.IsNullOrWhiteSpace(input.Title) ||
            string.IsNullOrWhiteSpace(input.ServiceDescription))
            return Problem(statusCode: 400, title: "Invalid request");
        item!.Title = input.Title.Trim(); item.Location = Clean(input.Location);
        item.Description = Clean(input.Description);
        item.ServiceDescription = input.ServiceDescription.Trim();
        item.Remarks = Clean(input.Remarks);
        item.UpdatedUtc = item.ServiceRequest!.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); return NoContent();
    }
    [HttpDelete("{itemId:guid}")]
    public async Task<IActionResult> Delete(Guid itemId, CancellationToken ct)
    {
        var item = await Load(itemId, ct); var denied = await Editable(item, ct);
        if (denied is not null) return denied;
        var request = item!.ServiceRequest!; db.Remove(item); await db.SaveChangesAsync(ct);
        var remaining = await db.ServiceRequestItems.Where(x => x.ServiceRequestId == request.Id)
            .OrderBy(x => x.SequenceNumber).ToListAsync(ct);
        for (var i = 0; i < remaining.Count; i++) remaining[i].SequenceNumber = -(i + 1);
        await db.SaveChangesAsync(ct);
        for (var i = 0; i < remaining.Count; i++) remaining[i].SequenceNumber = i + 1;
        request.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); return NoContent();
    }
    private Task<ServiceRequestItem?> Load(Guid id, CancellationToken ct) =>
        db.ServiceRequestItems.Include(x => x.ServiceRequest)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
    private async Task<IActionResult?> Editable(ServiceRequestItem? item, CancellationToken ct)
    {
        if (item is null) return Problem(statusCode: 404, title: "Request item not found");
        var request = item.ServiceRequest!;
        if (!await access.CanAccessVesselAsync(User, request.VesselId, ct))
            return Problem(statusCode: 403, title: "Access denied");
        if (User.IsInRole("Crew") && (request.CreatedByUserId != CurrentUser() ||
            request.Status != ServiceRequestStatus.Draft))
            return Problem(statusCode: 403, title: "Access denied");
        return request.Status is ServiceRequestStatus.Draft or ServiceRequestStatus.Ready
            ? null : Problem(statusCode: 409, title: "Request is read-only");
    }
    private Guid CurrentUser() => Guid.TryParse(User.FindFirstValue(
        ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private static string? Clean(string? x) => string.IsNullOrWhiteSpace(x) ? null : x.Trim();
}

[ApiController, Authorize, Route("api/service-requests")]
public sealed class ServiceRequestAttachmentsController(
    FsmDbContext db, EquipmentDocumentAccessService access,
    ServiceRequestStorageService storage) : ControllerBase
{
    [HttpPost("{id:guid}/attachments")]
    [RequestSizeLimit(25L * 1024 * 1024 + 1024)]
    public async Task<IActionResult> Upload(Guid id, IFormFile file,
        string? description, CancellationToken ct)
    {
        var request = await db.ServiceRequests.SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(request, ct); if (denied is not null) return denied;
        (string path, string mime, long size) saved;
        try { saved = await storage.SaveAttachmentAsync(id, file, ct); }
        catch (InvalidDataException ex) { return Problem(statusCode: 400,
            title: "Invalid attachment", detail: ex.Message); }
        var attachment = new ServiceRequestAttachment
        {
            Id = Guid.NewGuid(), ServiceRequestId = id,
            OriginalFileName = SafeName(file.FileName), StoredFileName = saved.path,
            MimeType = saved.mime, FileSize = saved.size, Description = Clean(description),
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = User.FindFirstValue(ClaimTypes.Name) ?? "User"
        };
        try
        {
            db.Add(attachment); request!.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch { SafeDelete(saved.path); throw; }
        return Created($"/api/service-request-attachments/{attachment.Id}",
            new ServiceRequestAttachmentDto(attachment.Id, attachment.OriginalFileName,
                attachment.MimeType, attachment.FileSize, attachment.Description,
                attachment.CreatedUtc, attachment.CreatedBy));
    }
    private async Task<IActionResult?> Editable(ServiceRequest? request, CancellationToken ct)
    {
        if (request is null) return Problem(statusCode: 404, title: "Service request not found");
        if (!await access.CanAccessVesselAsync(User, request.VesselId, ct))
            return Problem(statusCode: 403, title: "Access denied");
        if (User.IsInRole("Crew") && (request.CreatedByUserId != CurrentUser() ||
            request.Status != ServiceRequestStatus.Draft))
            return Problem(statusCode: 403, title: "Access denied");
        return request.Status is ServiceRequestStatus.Draft or ServiceRequestStatus.Ready
            ? null : Problem(statusCode: 409, title: "Request is read-only");
    }
    private Guid CurrentUser() => Guid.TryParse(User.FindFirstValue(
        ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private static string SafeName(string value) => DocumentStorageService.SanitizeFileName(
        value.Replace('\\', '/').Split('/').LastOrDefault() ?? "attachment");
    private static string? Clean(string? x) => string.IsNullOrWhiteSpace(x) ? null : x.Trim();
    private void SafeDelete(string path) { try { storage.DeleteIfExists(path); } catch { } }
}

[ApiController, Authorize, Route("api/service-request-attachments")]
public sealed class ServiceRequestAttachmentController(
    FsmDbContext db, EquipmentDocumentAccessService access,
    ServiceRequestStorageService storage) : ControllerBase
{
    [HttpPut("{attachmentId:guid}")]
    public async Task<IActionResult> Update(Guid attachmentId,
        ServiceRequestAttachmentUpdate input, CancellationToken ct)
    {
        var row = await Load(attachmentId, ct); var denied = await Editable(row, ct);
        if (denied is not null) return denied;
        row!.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        row.ServiceRequest!.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); return NoContent();
    }
    [HttpDelete("{attachmentId:guid}")]
    public async Task<IActionResult> Delete(Guid attachmentId, CancellationToken ct)
    {
        var row = await Load(attachmentId, ct); var denied = await Editable(row, ct);
        if (denied is not null) return denied;
        var path = row!.StoredFileName; db.Remove(row);
        row.ServiceRequest!.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
        try { storage.DeleteIfExists(path); } catch { } return NoContent();
    }
    [HttpGet("{attachmentId:guid}/download")]
    public async Task<IActionResult> Download(Guid attachmentId, CancellationToken ct)
    {
        var row = await Load(attachmentId, ct, false);
        if (row is null) return Problem(statusCode: 404, title: "Attachment not found");
        if (!await access.CanAccessVesselAsync(User, row.ServiceRequest!.VesselId, ct))
            return Problem(statusCode: 403, title: "Access denied");
        try
        {
            var path = storage.GetAbsolutePath(row.StoredFileName);
            if (!System.IO.File.Exists(path)) return Problem(statusCode: 404, title: "Attachment file not found");
            return PhysicalFile(path, row.MimeType, row.OriginalFileName, true);
        }
        catch (InvalidOperationException) { return Problem(statusCode: 400, title: "Invalid stored attachment"); }
    }
    private async Task<ServiceRequestAttachment?> Load(Guid id, CancellationToken ct, bool tracked = true)
    {
        IQueryable<ServiceRequestAttachment> q = db.ServiceRequestAttachments.Include(x => x.ServiceRequest);
        if (!tracked) q = q.AsNoTracking(); return await q.SingleOrDefaultAsync(x => x.Id == id, ct);
    }
    private async Task<IActionResult?> Editable(ServiceRequestAttachment? row, CancellationToken ct)
    {
        if (row is null) return Problem(statusCode: 404, title: "Attachment not found");
        var request = row.ServiceRequest!;
        if (!await access.CanAccessVesselAsync(User, request.VesselId, ct))
            return Problem(statusCode: 403, title: "Access denied");
        if (User.IsInRole("Crew") && (request.CreatedByUserId != CurrentUser() ||
            request.Status != ServiceRequestStatus.Draft))
            return Problem(statusCode: 403, title: "Access denied");
        return request.Status is ServiceRequestStatus.Draft or ServiceRequestStatus.Ready
            ? null : Problem(statusCode: 409, title: "Request is read-only");
    }
    private Guid CurrentUser() => Guid.TryParse(User.FindFirstValue(
        ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}

[ApiController, Authorize, Route("api/service-request-pdfs")]
public sealed class ServiceRequestPdfsController(
    FsmDbContext db, EquipmentDocumentAccessService access,
    ServiceRequestStorageService storage) : ControllerBase
{
    [HttpGet("{pdfId:guid}/download")]
    public async Task<IActionResult> Download(Guid pdfId, CancellationToken ct)
    {
        var pdf = await db.ServiceRequestPdfs.AsNoTracking().Include(x => x.ServiceRequest)
            .SingleOrDefaultAsync(x => x.Id == pdfId, ct);
        if (pdf is null) return Problem(statusCode: 404, title: "PDF not found");
        if (!await access.CanAccessVesselAsync(User, pdf.ServiceRequest!.VesselId, ct))
            return Problem(statusCode: 403, title: "Access denied");
        try
        {
            var path = storage.GetAbsolutePath(pdf.StoredFileName);
            if (!System.IO.File.Exists(path)) return Problem(statusCode: 404, title: "PDF file not found");
            return PhysicalFile(path, pdf.MimeType, pdf.FileName, true);
        }
        catch (InvalidOperationException) { return Problem(statusCode: 400, title: "Invalid stored PDF"); }
    }
}
