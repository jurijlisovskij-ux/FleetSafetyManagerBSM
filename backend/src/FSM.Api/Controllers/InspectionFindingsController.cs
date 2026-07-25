using FSM.Api.Models;
using FSM.Api.Services;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController, Authorize, Route("api/inspection-findings")]
public sealed class InspectionFindingsController(
    FsmDbContext db, EquipmentDocumentAccessService access,
    InspectionStorageService storage) : ControllerBase
{
    [HttpPut("{findingId:guid}")]
    public async Task<IActionResult> Update(Guid findingId,
        InspectionFindingRequest request, CancellationToken ct)
    {
        var finding = await db.InspectionFindings.Include(x => x.InspectionReport)
            .SingleOrDefaultAsync(x => x.Id == findingId, ct);
        var denied = await Editable(finding, ct); if (denied is not null) return denied;
        if (string.IsNullOrWhiteSpace(request.EquipmentOrArea) ||
            string.IsNullOrWhiteSpace(request.Description))
            return Invalid("Equipment/area and description are required.");
        finding!.Department = request.Department;
        finding.EquipmentOrArea = request.EquipmentOrArea.Trim();
        finding.ExactLocation = Clean(request.ExactLocation);
        finding.Description = request.Description.Trim();
        finding.Priority = request.Priority; finding.Status = request.Status;
        finding.InspectorRemarks = Clean(request.InspectorRemarks);
        finding.UpdatedUtc = finding.InspectionReport!.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpDelete("{findingId:guid}")]
    public async Task<IActionResult> Delete(Guid findingId, CancellationToken ct)
    {
        var finding = await db.InspectionFindings.Include(x => x.InspectionReport)
            .Include(x => x.Photos).SingleOrDefaultAsync(x => x.Id == findingId, ct);
        var denied = await Editable(finding, ct); if (denied is not null) return denied;
        var report = finding!.InspectionReport!;
        var paths = finding.Photos.Select(x => x.StoredFileName).ToList();
        db.Remove(finding); await db.SaveChangesAsync(ct);
        var remaining = await db.InspectionFindings.Where(x => x.InspectionReportId == report.Id)
            .OrderBy(x => x.SequenceNumber).ToListAsync(ct);
        for (var i = 0; i < remaining.Count; i++) remaining[i].SequenceNumber = -(i + 1);
        await db.SaveChangesAsync(ct);
        for (var i = 0; i < remaining.Count; i++) remaining[i].SequenceNumber = i + 1;
        report.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
        foreach (var path in paths) SafeDelete(path);
        return NoContent();
    }

    [HttpPost("{findingId:guid}/photos")]
    [RequestSizeLimit(15L * 1024 * 1024 + 1024)]
    public async Task<IActionResult> Upload(Guid findingId, IFormFile file,
        CancellationToken ct)
    {
        var finding = await db.InspectionFindings.Include(x => x.InspectionReport)
            .SingleOrDefaultAsync(x => x.Id == findingId, ct);
        var denied = await Editable(finding, ct); if (denied is not null) return denied;
        (string path, string mime, long size) saved;
        try { saved = await storage.SavePhotoAsync(
            finding!.InspectionReportId, findingId, file, ct); }
        catch (InvalidDataException exception) { return Invalid(exception.Message); }
        var photo = new InspectionPhoto
        {
            Id = Guid.NewGuid(), FindingId = findingId,
            OriginalFileName = SafeOriginalName(file.FileName),
            StoredFileName = saved.path, MimeType = saved.mime, FileSize = saved.size,
            CreatedUtc = DateTime.UtcNow
        };
        try
        {
            db.Add(photo); finding!.InspectionReport!.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch { SafeDelete(saved.path); throw; }
        return Created($"/api/inspection-photos/{photo.Id}",
            new InspectionPhotoDto(photo.Id, photo.OriginalFileName,
                photo.MimeType, photo.FileSize, photo.Caption, photo.CreatedUtc));
    }

    private async Task<IActionResult?> Editable(InspectionFinding? finding, CancellationToken ct)
    {
        if (finding is null) return Problem(statusCode: 404, title: "Finding not found");
        if ((!User.IsInRole("OfficeAdmin") && !User.IsInRole("Superintendent")) ||
            !await access.CanAccessVesselAsync(User, finding.InspectionReport!.VesselId, ct))
            return Problem(statusCode: 403, title: "Access denied");
        return finding.InspectionReport.State == InspectionReportState.Finalized
            ? Problem(statusCode: 409, title: "Finalized report is read-only") : null;
    }
    private ObjectResult Invalid(string detail) => Problem(statusCode: 400,
        title: "Invalid request", detail: detail);
    private static string? Clean(string? x) => string.IsNullOrWhiteSpace(x) ? null : x.Trim();
    private static string SafeOriginalName(string name)
    {
        var leaf = name.Replace('\\', '/').Split('/').LastOrDefault() ?? "photo";
        return DocumentStorageService.SanitizeFileName(leaf);
    }
    private void SafeDelete(string path) { try { storage.DeleteIfExists(path); } catch { } }
}

[ApiController, Authorize, Route("api/inspection-photos")]
public sealed class InspectionPhotosController(
    FsmDbContext db, EquipmentDocumentAccessService access,
    InspectionStorageService storage) : ControllerBase
{
    [HttpPut("{photoId:guid}")]
    public async Task<IActionResult> Update(Guid photoId,
        InspectionPhotoUpdateRequest request, CancellationToken ct)
    {
        var photo = await Load(photoId, ct);
        var denied = await Editable(photo, ct); if (denied is not null) return denied;
        photo!.Caption = string.IsNullOrWhiteSpace(request.Caption) ? null : request.Caption.Trim();
        photo.Finding!.InspectionReport!.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpDelete("{photoId:guid}")]
    public async Task<IActionResult> Delete(Guid photoId, CancellationToken ct)
    {
        var photo = await Load(photoId, ct);
        var denied = await Editable(photo, ct); if (denied is not null) return denied;
        var path = photo!.StoredFileName;
        db.Remove(photo); photo.Finding!.InspectionReport!.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        try { storage.DeleteIfExists(path); } catch { }
        return NoContent();
    }

    [HttpGet("{photoId:guid}/download")]
    public async Task<IActionResult> Download(Guid photoId, CancellationToken ct)
    {
        var photo = await Load(photoId, ct, false);
        if (photo is null) return Problem(statusCode: 404, title: "Photo not found");
        if (!await access.CanAccessVesselAsync(User,
            photo.Finding!.InspectionReport!.VesselId, ct))
            return Problem(statusCode: 403, title: "Access denied");
        if (User.IsInRole("Crew") &&
            photo.Finding.InspectionReport.State != InspectionReportState.Finalized)
            return Problem(statusCode: 403, title: "Access denied");
        try
        {
            var path = storage.GetAbsolutePath(photo.StoredFileName);
            if (!System.IO.File.Exists(path))
                return Problem(statusCode: 404, title: "Photo file not found");
            return PhysicalFile(path, photo.MimeType, photo.OriginalFileName, true);
        }
        catch (InvalidOperationException)
        {
            return Problem(statusCode: 400, title: "Invalid stored photo");
        }
    }

    private async Task<InspectionPhoto?> Load(Guid id, CancellationToken ct, bool tracked = true)
    {
        IQueryable<InspectionPhoto> query = db.InspectionPhotos.Include(x => x.Finding)
            .ThenInclude(x => x!.InspectionReport);
        if (!tracked) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.Id == id, ct);
    }
    private async Task<IActionResult?> Editable(InspectionPhoto? photo, CancellationToken ct)
    {
        if (photo is null) return Problem(statusCode: 404, title: "Photo not found");
        var report = photo.Finding!.InspectionReport!;
        if ((!User.IsInRole("OfficeAdmin") && !User.IsInRole("Superintendent")) ||
            !await access.CanAccessVesselAsync(User, report.VesselId, ct))
            return Problem(statusCode: 403, title: "Access denied");
        return report.State == InspectionReportState.Finalized
            ? Problem(statusCode: 409, title: "Finalized report is read-only") : null;
    }
}

[ApiController, Authorize, Route("api/inspection-report-pdfs")]
public sealed class InspectionReportPdfsController(
    FsmDbContext db, EquipmentDocumentAccessService access,
    InspectionStorageService storage) : ControllerBase
{
    [HttpGet("{pdfId:guid}/download")]
    public async Task<IActionResult> Download(Guid pdfId, CancellationToken ct)
    {
        var pdf = await db.InspectionReportPdfs.AsNoTracking()
            .Include(x => x.InspectionReport).SingleOrDefaultAsync(x => x.Id == pdfId, ct);
        if (pdf is null) return Problem(statusCode: 404, title: "PDF not found");
        if (!await access.CanAccessVesselAsync(User, pdf.InspectionReport!.VesselId, ct))
            return Problem(statusCode: 403, title: "Access denied");
        try
        {
            var path = storage.GetAbsolutePath(pdf.StoredFileName);
            if (!System.IO.File.Exists(path))
                return Problem(statusCode: 404, title: "PDF file not found");
            return PhysicalFile(path, pdf.MimeType, pdf.FileName, true);
        }
        catch (InvalidOperationException)
        {
            return Problem(statusCode: 400, title: "Invalid stored PDF");
        }
    }
}
