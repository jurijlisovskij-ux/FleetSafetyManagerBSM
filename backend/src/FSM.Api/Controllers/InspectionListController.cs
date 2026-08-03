using System.IO.Compression;
using FSM.Api.Authorization;
using FSM.Api.Services;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace FSM.Api.Controllers;

public sealed record GenerateInspectionListRequest(
    Guid VesselId,
    IReadOnlyList<Guid> EquipmentIds,
    string Format,
    bool IncludeCertificates,
    DateOnly? InspectionDate = null,
    string? Inspector = null,
    IReadOnlyList<InspectionResultRequest>? Results = null);
public sealed record InspectionResultRequest(Guid EquipmentId, string Result, string? Remarks, string? CorrectiveAction);
public sealed record CloseDefectRequest(DateOnly ClosureDate, string ClosedBy, string? ClosureRemarks);

[ApiController]
[Authorize(Roles = "OfficeAdmin,Superintendent,Crew")]
[Route("api/inspection-list")]
public sealed class InspectionListController(
    FsmDbContext db,
    InspectionListService inspectionListService,
    DocumentStorageService storage,
    VesselAccessService vesselAccess) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Generate(
        GenerateInspectionListRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EquipmentIds.Count == 0)
        {
            return BadRequest(new
            {
                message =
                    "Select at least one equipment item."
            });
        }

        if (!string.Equals(
                request.Format,
                "excel",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                request.Format,
                "pdf",
                StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                message =
                    "Format must be excel or pdf."
            });
        }

        if (!await vesselAccess.CanAccessVesselAsync(
            User,
            request.VesselId,
            cancellationToken))
        {
            return Forbid();
        }

        var vessel = await db.Vessels
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == request.VesselId,
                cancellationToken);

        if (vessel is null)
            return NotFound();

        var selectedIds = request.EquipmentIds
            .Distinct()
            .ToList();

        var equipment = await db.Equipment
            .AsNoTracking()
            .Where(x =>
                x.VesselId == request.VesselId &&
                selectedIds.Contains(x.Id))
            .OrderBy(x => x.Category)
            .ThenBy(x => x.EquipmentName)
            .ThenBy(x => x.Maker)
            .ThenBy(x => x.Model)
            .ToListAsync(cancellationToken);

        if (equipment.Count != selectedIds.Count)
        {
            return BadRequest(new
            {
                message =
                    "One or more selected equipment items are invalid."
            });
        }

        var isPdf = string.Equals(
            request.Format,
            "pdf",
            StringComparison.OrdinalIgnoreCase);

        var reportId = Guid.NewGuid();
        var reportNumber = $"{vessel.ImoNumber}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var inspectionDate = request.InspectionDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var inspector = string.IsNullOrWhiteSpace(request.Inspector) ? User.Identity?.Name ?? "Unknown" : request.Inspector.Trim();
        var results = request.Results ?? equipment.Select(x => new InspectionResultRequest(x.Id, "Not Applicable", null, null)).ToList();
        if (results.Any(x => !selectedIds.Contains(x.EquipmentId) || x.Result is not ("OK" or "Defect" or "Not Applicable")))
            return BadRequest(new { message = "Each selected item requires a valid OK, Defect or Not Applicable result." });
        var listFile = isPdf
            ? inspectionListService.GenerateCompletedPdf(vessel, reportNumber, inspectionDate, inspector,
                equipment.Select(x => (x, results.FirstOrDefault(r => r.EquipmentId == x.Id)?.Result ?? "Not Applicable", results.FirstOrDefault(r => r.EquipmentId == x.Id)?.Remarks)).ToList())
            : inspectionListService.GenerateExcel(
                vessel,
                equipment);

        if (isPdf)
        {
            var relativePath = await storage.SaveInspectionReportAsync(vessel.ImoNumber, reportId, listFile, cancellationToken);
            var report = new FSM.Domain.Entities.InspectionReport { Id = reportId, VesselId = vessel.Id, ReportNumber = reportNumber, InspectionDate = inspectionDate, Inspector = inspector, PdfPath = relativePath, PdfSha256 = Convert.ToHexString(SHA256.HashData(listFile)) };
            foreach (var item in equipment) { var result = results.FirstOrDefault(x => x.EquipmentId == item.Id); report.Items.Add(new FSM.Domain.Entities.InspectionReportItem { Id = Guid.NewGuid(), EquipmentId = item.Id, Result = result?.Result ?? "Not Applicable", Remarks = result?.Remarks?.Trim(), CorrectiveAction = result?.CorrectiveAction?.Trim() }); }
            db.InspectionReports.Add(report); await db.SaveChangesAsync(cancellationToken);
        }

        var extension = isPdf ? ".pdf" : ".xlsx";
        var contentType = isPdf
            ? "application/pdf"
            : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        var safeVesselName = string.Concat(
            vessel.VesselName.Select(character =>
                Path.GetInvalidFileNameChars()
                    .Contains(character)
                    ? '_'
                    : character));

        var dateText =
            DateTime.UtcNow.ToString("yyyy-MM-dd");

        var downloadName =
            $"Inspection List - {safeVesselName} - " +
            $"{dateText}{extension}";

        if (!request.IncludeCertificates)
        {
            return File(
                listFile,
                contentType,
                downloadName);
        }

        await using var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(
            zipStream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            var listEntry = archive.CreateEntry(
                $"Inspection List{extension}",
                CompressionLevel.Optimal);

            await using (var output = listEntry.Open())
            {
                await output.WriteAsync(
                    listFile,
                    cancellationToken);
            }

            foreach (var item in equipment)
            {
                if (string.IsNullOrWhiteSpace(
                    item.CurrentCertificatePath))
                {
                    continue;
                }

                AddCertificateToArchive(
                    archive,
                    storage,
                    item);
            }
        }

        return File(
            zipStream.ToArray(),
            "application/zip",
            $"Inspection List - {safeVesselName} - " +
            $"{dateText}.zip");
    }

    [HttpGet("vessels/{vesselId:guid}/history")]
    public async Task<IActionResult> History(Guid vesselId, [FromQuery] DateOnly? date, [FromQuery] string? reportNumber, [FromQuery] string? category, CancellationToken ct)
    {
        if (!await vesselAccess.CanAccessVesselAsync(User, vesselId, ct)) return Forbid();
        var query = db.InspectionReports.AsNoTracking().Where(x => x.VesselId == vesselId).Include(x => x.Items).ThenInclude(x => x.Equipment).AsQueryable();
        if (date is not null) query = query.Where(x => x.InspectionDate == date); if (!string.IsNullOrWhiteSpace(reportNumber)) query = query.Where(x => x.ReportNumber.Contains(reportNumber));
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Items.Any(i => i.Equipment!.Category.ToString() == category));
        return Ok(await query.OrderByDescending(x => x.InspectionDate).Select(x => new { x.Id, x.ReportNumber, x.InspectionDate, x.Inspector, x.PdfSha256, categories = x.Items.Select(i => i.Equipment!.Category).Distinct(), defects = x.Items.Count(i => i.Result == "Defect" && i.ClosureDate == null) }).ToListAsync(ct));
    }

    [HttpGet("reports/{reportId:guid}/pdf")]
    public async Task<IActionResult> Download(Guid reportId, CancellationToken ct)
    {
        var report = await db.InspectionReports.AsNoTracking().Include(x => x.Vessel).SingleOrDefaultAsync(x => x.Id == reportId, ct); if (report is null) return NotFound();
        if (!await vesselAccess.CanAccessVesselAsync(User, report.VesselId, ct)) return Forbid(); var path = storage.GetAbsolutePath(report.PdfPath); if (!System.IO.File.Exists(path)) return NotFound(new { message = "Inspection PDF file is missing." });
        return PhysicalFile(path, "application/pdf", $"Inspection-{report.ReportNumber}.pdf");
    }

    [HttpPut("reports/{reportId:guid}/defects/{itemId:guid}/close")]
    public async Task<IActionResult> CloseDefect(Guid reportId, Guid itemId, CloseDefectRequest request, CancellationToken ct)
    {
        var item = await db.InspectionReportItems.Include(x => x.InspectionReport).SingleOrDefaultAsync(x => x.Id == itemId && x.InspectionReportId == reportId, ct); if (item is null) return NotFound();
        if (!await vesselAccess.CanAccessVesselAsync(User, item.InspectionReport!.VesselId, ct)) return Forbid(); if (item.Result != "Defect") return BadRequest(new { message = "Only defects can be closed." });
        if (string.IsNullOrWhiteSpace(request.ClosedBy)) return BadRequest(new { message = "Closed by is required." }); item.ClosureDate = request.ClosureDate; item.ClosedBy = request.ClosedBy.Trim(); item.ClosureRemarks = request.ClosureRemarks?.Trim(); item.LastModifiedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpPost("reports/{reportId:guid}/items/{itemId:guid}/attachment")]
    public async Task<IActionResult> UploadAttachment(Guid reportId, Guid itemId, IFormFile file, [FromQuery] bool closure = false, CancellationToken ct = default)
    {
        var item = await db.InspectionReportItems.Include(x => x.InspectionReport).ThenInclude(x => x!.Vessel).SingleOrDefaultAsync(x => x.Id == itemId && x.InspectionReportId == reportId, ct); if (item is null) return NotFound();
        if (!await vesselAccess.CanAccessVesselAsync(User, item.InspectionReport!.VesselId, ct)) return Forbid();
        try { var path = await storage.SaveInspectionAttachmentAsync(item.InspectionReport.Vessel!.ImoNumber, item.Id, closure ? "closure" : "inspection", file, ct); if (closure) item.ClosureAttachmentPath = path; else item.AttachmentPath = path; item.LastModifiedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); return Ok(new { path }); }
        catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }
    }

    private static void AddCertificateToArchive(
        ZipArchive archive,
        DocumentStorageService storage,
        FSM.Domain.Entities.Equipment equipment)
    {
        var relativePath =
            equipment.CurrentCertificatePath;

        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var absolutePath =
            storage.GetAbsolutePath(relativePath);

        if (!System.IO.File.Exists(absolutePath))
            return;

        var safeEquipmentName = string.Concat(
            equipment.EquipmentName.Select(character =>
                Path.GetInvalidFileNameChars()
                    .Contains(character)
                    ? '_'
                    : character));

        var originalName =
            equipment.CurrentCertificateFileName;

        var extension =
            Path.GetExtension(originalName);

        if (string.IsNullOrWhiteSpace(extension))
            extension = ".pdf";

        var entry = archive.CreateEntry(
            $"Certificates/" +
            $"{safeEquipmentName}_{equipment.Id:N}" +
            $"{extension}",
            CompressionLevel.Optimal);

        using var input =
            System.IO.File.OpenRead(absolutePath);

        using var output = entry.Open();
        input.CopyTo(output);
    }

}
