using System.Data;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FSM.Api.Models;
using FSM.Api.Services;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/inspection-reports")]
public sealed partial class InspectionReportsController(
    FsmDbContext db, EquipmentDocumentAccessService access,
    InspectionStorageService storage, InspectionPdfService pdfService,
    SystemAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InspectionReportListDto>>> List(
        CancellationToken ct)
    {
        var query = db.InspectionReports.AsNoTracking();
        if (!User.IsInRole("OfficeAdmin"))
        {
            if (!TryUserId(out var userId)) return ForbidProblem();
            query = query.Where(x => db.UserVessels.Any(a =>
                a.UserId == userId && a.VesselId == x.VesselId));
        }
        if (User.IsInRole("Crew"))
            query = query.Where(x => x.State == InspectionReportState.Finalized);
        return Ok(await query.OrderByDescending(x => x.InspectionDate)
            .ThenByDescending(x => x.ReportNumber)
            .Select(x => new InspectionReportListDto(x.Id, x.ReportNumber,
                x.VesselId, x.Vessel!.VesselName, x.InspectionDate,
                x.InspectorName, x.InspectionType, x.State, x.Findings.Count,
                x.Findings.Count(f => f.Status == InspectionFindingStatus.Open),
                x.CreatedUtc)).ToListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var report = await LoadReport(id, false, ct);
        if (report is null) return NotFoundProblem();
        if (!await CanAccess(report.VesselId, ct)) return ForbidProblem();
        if (User.IsInRole("Crew") && report.State != InspectionReportState.Finalized)
            return ForbidProblem();
        return Ok(Map(report));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        InspectionReportCreateRequest request, CancellationToken ct)
    {
        if (!CanEditRole()) return ForbidProblem();
        if (!ValidHeader(request.InspectorName)) return BadRequestProblem("Inspector name is required.");
        var vessel = await db.Vessels.SingleOrDefaultAsync(x => x.Id == request.VesselId, ct);
        if (vessel is null) return Problem(statusCode: 404, title: "Vessel not found");
        if (!await CanAccess(vessel.Id, ct)) return ForbidProblem();

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, ct);
        var prefix = $"IR-{VesselCode(vessel.VesselName)}-{request.InspectionDate.Year}-";
        var numbers = await db.InspectionReports.AsNoTracking()
            .Where(x => x.VesselId == vessel.Id && x.ReportNumber.StartsWith(prefix))
            .Select(x => x.ReportNumber).ToListAsync(ct);
        var next = numbers.Select(x => int.TryParse(x[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty().Max() + 1;
        var now = DateTime.UtcNow;
        var report = new InspectionReport
        {
            Id = Guid.NewGuid(), VesselId = vessel.Id,
            ReportNumber = $"{prefix}{next:000}", InspectionDate = request.InspectionDate,
            InspectorName = request.InspectorName.Trim(),
            InspectionType = request.InspectionType,
            GeneralRemarks = Clean(request.GeneralRemarks),
            State = InspectionReportState.Draft, CreatedUtc = now, UpdatedUtc = now
        };
        db.InspectionReports.Add(report);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            return Problem(statusCode: 409, title: "Report number conflict",
                detail: "A report with this number already exists. Try again.");
        }
        report.Vessel = vessel;
        return CreatedAtAction(nameof(Get), new { id = report.Id }, Map(report));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id,
        InspectionReportUpdateRequest request, CancellationToken ct)
    {
        var report = await db.InspectionReports.SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(report, ct); if (denied is not null) return denied;
        if (!ValidHeader(request.InspectorName)) return BadRequestProblem("Inspector name is required.");
        report!.InspectionDate = request.InspectionDate;
        report.InspectorName = request.InspectorName.Trim();
        report.InspectionType = request.InspectionType;
        report.GeneralRemarks = Clean(request.GeneralRemarks);
        report.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var report = await db.InspectionReports.Include(x => x.Findings)
            .ThenInclude(x => x.Photos).Include(x => x.PdfVersions)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(report, ct); if (denied is not null) return denied;
        var paths = report!.Findings.SelectMany(x => x.Photos).Select(x => x.StoredFileName)
            .Concat(report.PdfVersions.Select(x => x.StoredFileName)).ToList();
        db.Remove(report); await db.SaveChangesAsync(ct);
        foreach (var path in paths) SafeDelete(path);
        return NoContent();
    }

    [HttpPost("{id:guid}/findings")]
    public async Task<IActionResult> AddFinding(Guid id,
        InspectionFindingRequest request, CancellationToken ct)
    {
        var report = await db.InspectionReports.Include(x => x.Findings)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(report, ct); if (denied is not null) return denied;
        if (!ValidFinding(request)) return BadRequestProblem("Equipment/area and description are required.");
        var now = DateTime.UtcNow;
        var finding = new InspectionFinding
        {
            Id = Guid.NewGuid(), InspectionReportId = id,
            SequenceNumber = report!.Findings.Select(x => x.SequenceNumber).DefaultIfEmpty().Max() + 1,
            Department = request.Department, EquipmentOrArea = request.EquipmentOrArea.Trim(),
            ExactLocation = Clean(request.ExactLocation), Description = request.Description.Trim(),
            Priority = request.Priority, Status = request.Status,
            InspectorRemarks = Clean(request.InspectorRemarks), CreatedUtc = now, UpdatedUtc = now
        };
        db.Add(finding); report.UpdatedUtc = now; await db.SaveChangesAsync(ct);
        return Created($"/api/inspection-findings/{finding.Id}", Map(finding));
    }

    [HttpPost("{id:guid}/findings/reorder")]
    public async Task<IActionResult> Reorder(Guid id, ReorderFindingsRequest request,
        CancellationToken ct)
    {
        var report = await db.InspectionReports.Include(x => x.Findings)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(report, ct); if (denied is not null) return denied;
        if (request.FindingIds.Count != report!.Findings.Count ||
            request.FindingIds.Distinct().Count() != request.FindingIds.Count ||
            request.FindingIds.Except(report.Findings.Select(x => x.Id)).Any())
            return BadRequestProblem("Finding order must contain every report finding exactly once.");
        // Temporary negative values avoid the unique index during a single SaveChanges.
        foreach (var finding in report.Findings)
            finding.SequenceNumber = -finding.SequenceNumber;
        await db.SaveChangesAsync(ct);
        for (var index = 0; index < request.FindingIds.Count; index++)
            report.Findings.Single(x => x.Id == request.FindingIds[index]).SequenceNumber = index + 1;
        report.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/finalize")]
    public async Task<IActionResult> FinalizeReport(Guid id, CancellationToken ct)
    {
        var report = await db.InspectionReports.SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(report, ct); if (denied is not null) return denied;
        report!.State = InspectionReportState.Finalized;
        report.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, "InspectionFinalized", "InspectionReport",
            report.Id.ToString(), report.ReportNumber, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid id, ReopenInspectionRequest request,
        CancellationToken ct)
    {
        if (!User.IsInRole("OfficeAdmin")) return ForbidProblem();
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequestProblem("A reopening reason is required.");
        var report = await db.InspectionReports.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return NotFoundProblem();
        if (report.State != InspectionReportState.Finalized)
            return ConflictProblem("Only a finalized report can be reopened.");
        report.State = InspectionReportState.Draft;
        report.ReopenedUtc = report.UpdatedUtc = DateTime.UtcNow;
        report.ReopenedBy = User.FindFirstValue(ClaimTypes.Name) ?? "Administrator";
        report.ReopeningReason = request.Reason.Trim();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, "InspectionReopened", "InspectionReport",
            report.Id.ToString(), request.Reason.Trim(), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/generate-pdf")]
    public async Task<IActionResult> GeneratePdf(Guid id, CancellationToken ct)
    {
        var report = await LoadReport(id, true, ct);
        if (report is null) return NotFoundProblem();
        if (!CanEditRole() || !await CanAccess(report.VesselId, ct)) return ForbidProblem();
        if (report.State != InspectionReportState.Finalized)
            return ConflictProblem("Finalize the report before generating its PDF.");
        var photoPaths = new Dictionary<Guid, string>();
        try
        {
            foreach (var photo in report.Findings.SelectMany(x => x.Photos))
                photoPaths[photo.Id] = storage.GetAbsolutePath(photo.StoredFileName);
        }
        catch (InvalidOperationException)
        {
            return Problem(statusCode: 400, title: "Invalid stored photo",
                detail: "A report photograph has an invalid stored path.");
        }
        byte[] bytes;
        try { bytes = pdfService.Generate(report, photoPaths); }
        catch (Exception) { return Problem(statusCode: 500, title: "PDF generation failed"); }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var version = await db.InspectionReportPdfs.Where(x => x.InspectionReportId == id)
            .Select(x => (int?)x.VersionNumber).MaxAsync(ct) ?? 0;
        version++;
        var relative = await storage.SavePdfAsync(id, version, bytes, ct);
        try
        {
            await db.InspectionReportPdfs.Where(x => x.InspectionReportId == id && x.IsCurrent)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsCurrent, false), ct);
            var row = new InspectionReportPdf
            {
                Id = Guid.NewGuid(), InspectionReportId = id, VersionNumber = version,
                FileName = $"{report.ReportNumber}-v{version}.pdf", StoredFileName = relative,
                MimeType = "application/pdf", FileSize = bytes.LongLength,
                GeneratedUtc = DateTime.UtcNow,
                GeneratedBy = User.FindFirstValue(ClaimTypes.Name) ?? "User", IsCurrent = true
            };
            db.Add(row); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Ok(Map(row));
        }
        catch
        {
            await transaction.RollbackAsync(ct); SafeDelete(relative); throw;
        }
    }

    [HttpGet("{id:guid}/pdf-versions")]
    public async Task<IActionResult> PdfVersions(Guid id, CancellationToken ct)
    {
        var report = await db.InspectionReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (report is null) return NotFoundProblem();
        if (!await CanAccess(report.VesselId, ct)) return ForbidProblem();
        return Ok(await db.InspectionReportPdfs.AsNoTracking()
            .Where(x => x.InspectionReportId == id)
            .OrderByDescending(x => x.VersionNumber).Select(x =>
                new InspectionPdfDto(x.Id, x.VersionNumber, x.FileName, x.FileSize,
                    x.GeneratedUtc, x.GeneratedBy, x.IsCurrent)).ToListAsync(ct));
    }

    private async Task<InspectionReport?> LoadReport(Guid id, bool tracked, CancellationToken ct)
    {
        IQueryable<InspectionReport> query = db.InspectionReports.Include(x => x.Vessel)
            .Include(x => x.Findings).ThenInclude(x => x.Photos);
        if (!tracked) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.Id == id, ct);
    }
    private async Task<IActionResult?> Editable(InspectionReport? report, CancellationToken ct)
    {
        if (report is null) return NotFoundProblem();
        if (!CanEditRole() || !await CanAccess(report.VesselId, ct)) return ForbidProblem();
        return report.State == InspectionReportState.Finalized
            ? ConflictProblem("Finalized reports are read-only.") : null;
    }
    private bool CanEditRole() => User.IsInRole("OfficeAdmin") || User.IsInRole("Superintendent");
    private Task<bool> CanAccess(Guid vesselId, CancellationToken ct) =>
        access.CanAccessVesselAsync(User, vesselId, ct);
    private bool TryUserId(out Guid id) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out id);
    private static bool ValidHeader(string name) => !string.IsNullOrWhiteSpace(name);
    private static bool ValidFinding(InspectionFindingRequest x) =>
        !string.IsNullOrWhiteSpace(x.EquipmentOrArea) && !string.IsNullOrWhiteSpace(x.Description);
    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string VesselCode(string name)
    {
        var code = NonCodeCharacters().Replace(name.ToUpperInvariant(), "");
        return string.IsNullOrWhiteSpace(code) ? "VESSEL" : code[..Math.Min(code.Length, 20)];
    }
    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex NonCodeCharacters();
    private ObjectResult NotFoundProblem() => Problem(statusCode: 404,
        title: "Inspection report not found");
    private ObjectResult ForbidProblem() => Problem(statusCode: 403,
        title: "Access denied", detail: "The current user cannot access this resource.");
    private ObjectResult BadRequestProblem(string detail) => Problem(statusCode: 400,
        title: "Invalid request", detail: detail);
    private ObjectResult ConflictProblem(string detail) => Problem(statusCode: 409,
        title: "Inspection report conflict", detail: detail);
    private void SafeDelete(string path) { try { storage.DeleteIfExists(path); } catch { } }

    internal static InspectionFindingDto Map(InspectionFinding x) =>
        new(x.Id, x.SequenceNumber, x.Department, x.EquipmentOrArea,
            x.ExactLocation, x.Description, x.Priority, x.Status,
            x.InspectorRemarks, x.CreatedUtc, x.UpdatedUtc,
            x.Photos.OrderBy(p => p.CreatedUtc).Select(p => new InspectionPhotoDto(
                p.Id, p.OriginalFileName, p.MimeType, p.FileSize,
                p.Caption, p.CreatedUtc)).ToList());
    private static InspectionReportDto Map(InspectionReport x) =>
        new(x.Id, x.ReportNumber, x.VesselId, x.Vessel!.VesselName,
            x.Vessel.ImoNumber, x.InspectionDate, x.InspectorName,
            x.InspectionType, x.GeneralRemarks, x.State, x.CreatedUtc,
            x.UpdatedUtc, x.ReopenedUtc, x.ReopenedBy, x.ReopeningReason,
            x.Findings.OrderBy(f => f.SequenceNumber).Select(Map).ToList());
    private static InspectionPdfDto Map(InspectionReportPdf x) =>
        new(x.Id, x.VersionNumber, x.FileName, x.FileSize,
            x.GeneratedUtc, x.GeneratedBy, x.IsCurrent);
}
