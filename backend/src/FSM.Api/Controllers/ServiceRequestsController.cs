using System.Data;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FSM.Api.Models;
using FSM.Api.Services;
using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController, Authorize, Route("api/service-requests")]
public sealed partial class ServiceRequestsController(
    FsmDbContext db, EquipmentDocumentAccessService access,
    ServiceRequestStorageService storage, ServiceRequestPdfService pdfService,
    SystemAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var query = db.ServiceRequests.AsNoTracking();
        if (!User.IsInRole("OfficeAdmin"))
        {
            if (!UserId(out var userId)) return Forbidden();
            query = query.Where(x => db.UserVessels.Any(a =>
                a.UserId == userId && a.VesselId == x.VesselId));
        }
        if (User.IsInRole("Crew") && UserId(out var crewId))
            query = query.Where(x => x.CreatedByUserId == crewId ||
                x.Status == ServiceRequestStatus.Sent ||
                x.Status == ServiceRequestStatus.Completed ||
                x.Status == ServiceRequestStatus.Cancelled);
        return Ok(await query.OrderByDescending(x => x.RequestDate)
            .ThenByDescending(x => x.RequestNumber).Select(x =>
                new ServiceRequestListDto(x.Id, x.RequestNumber, x.VesselId,
                    x.Vessel!.VesselName, x.RequestDate, x.Subject,
                    x.PreferredPort, x.Status, x.Items.Count, x.RequestedBy,
                    x.UpdatedUtc)).ToListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var request = await Load(id, false, ct);
        if (request is null) return Missing();
        if (!await CanView(request, ct)) return Forbidden();
        return Ok(Map(request));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateManagedServiceRequest input, CancellationToken ct)
    {
        if (!ValidHeader(input.Subject, input.ContactName, input.ContactEmail))
            return Invalid("Subject, contact name, and contact email are required.");
        if (!UserId(out var userId)) return Forbidden();
        var vessel = await db.Vessels.SingleOrDefaultAsync(x => x.Id == input.VesselId, ct);
        if (vessel is null) return Problem(statusCode: 404, title: "Vessel not found");
        if (!await CanAccess(vessel.Id, ct)) return Forbidden();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, ct);
        var prefix = $"SR-{VesselCode(vessel.VesselName)}-{input.RequestDate.Year}-";
        var numbers = await db.ServiceRequests.AsNoTracking()
            .Where(x => x.VesselId == vessel.Id && x.RequestNumber.StartsWith(prefix))
            .Select(x => x.RequestNumber).ToListAsync(ct);
        var sequence = numbers.Select(x => int.TryParse(x[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty().Max() + 1;
        var now = DateTime.UtcNow;
        var requestedBy = User.FindFirstValue(ClaimTypes.Name) ?? input.ContactName.Trim();
        var request = new ServiceRequest
        {
            Id = Guid.NewGuid(), RequestNumber = $"{prefix}{sequence:000}",
            VesselId = vessel.Id, CreatedByUserId = userId,
            RequestDate = input.RequestDate, RequestedBy = requestedBy,
            ContactName = input.ContactName.Trim(), ContactEmail = input.ContactEmail.Trim(),
            ContactPhone = Clean(input.ContactPhone), PreferredPort = Clean(input.PreferredPort),
            ExpectedArrivalDate = input.ExpectedArrivalDate,
            ExpectedDepartureDate = input.ExpectedDepartureDate,
            ServiceCompany = Clean(input.ServiceCompany), AttentionTo = Clean(input.AttentionTo),
            Subject = input.Subject.Trim(), GeneralRemarks = Clean(input.GeneralRemarks),
            InvoicingDetails = vessel.InvoicingDetails,
            Status = ServiceRequestStatus.Draft, CreatedUtc = now, UpdatedUtc = now
        };
        db.Add(request);
        try
        {
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            return Conflict("A request with this number already exists. Try again.");
        }
        request.Vessel = vessel;
        return CreatedAtAction(nameof(Get), new { id = request.Id }, Map(request));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id,
        UpdateManagedServiceRequest input, CancellationToken ct)
    {
        var request = await db.ServiceRequests.SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(request, ct); if (denied is not null) return denied;
        if (!ValidHeader(input.Subject, input.ContactName, input.ContactEmail))
            return Invalid("Subject, contact name, and contact email are required.");
        request!.RequestDate = input.RequestDate; request.ContactName = input.ContactName.Trim();
        request.ContactEmail = input.ContactEmail.Trim(); request.ContactPhone = Clean(input.ContactPhone);
        request.PreferredPort = Clean(input.PreferredPort);
        request.ExpectedArrivalDate = input.ExpectedArrivalDate;
        request.ExpectedDepartureDate = input.ExpectedDepartureDate;
        request.ServiceCompany = Clean(input.ServiceCompany); request.AttentionTo = Clean(input.AttentionTo);
        request.Subject = input.Subject.Trim(); request.GeneralRemarks = Clean(input.GeneralRemarks);
        request.InvoicingDetails = Clean(input.InvoicingDetails); request.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var request = await Load(id, true, ct);
        var denied = await Editable(request, ct); if (denied is not null) return denied;
        if (request!.Status != ServiceRequestStatus.Draft)
            return Conflict("Only Draft requests may be deleted.");
        var paths = request.Attachments.Select(x => x.StoredFileName)
            .Concat(request.PdfVersions.Select(x => x.StoredFileName)).ToList();
        db.Remove(request); await db.SaveChangesAsync(ct);
        foreach (var path in paths) SafeDelete(path);
        return NoContent();
    }

    [HttpPost("{id:guid}/refresh-from-vessel")]
    public async Task<IActionResult> RefreshVessel(Guid id, CancellationToken ct)
    {
        var request = await db.ServiceRequests.Include(x => x.Vessel)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(request, ct); if (denied is not null) return denied;
        request!.InvoicingDetails = request.Vessel!.InvoicingDetails;
        if (!string.IsNullOrWhiteSpace(request.Vessel.AccountingEmail))
            request.ContactEmail = request.Vessel.AccountingEmail;
        request.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/available-equipment")]
    public async Task<IActionResult> AvailableEquipment(Guid id, CancellationToken ct)
    {
        var request = await db.ServiceRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (request is null) return Missing();
        if (!await CanView(request, ct)) return Forbidden();
        var equipment = await db.Equipment.AsNoTracking()
            .Where(x => x.VesselId == request.VesselId)
            .OrderBy(x => x.EquipmentName).ToListAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(equipment.Select(x => new
            {
                x.Id, x.EquipmentName, Category = x.Category == EquipmentCategory.RadioAndNavigation
                    ? "Radio & Navigation" : x.Category.ToString(),
                x.Maker, x.Model, x.SerialNumber, x.ExpiryDate,
                Responsible = x.InspectionBy == InspectionBy.ServiceCompany ? "Service Company" : "Crew",
                Status = x.CalculateStatus(today, 90).ToString()
            }).ToList());
    }

    [HttpGet("{id:guid}/available-findings")]
    public async Task<IActionResult> AvailableFindings(Guid id, CancellationToken ct)
    {
        var request = await db.ServiceRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (request is null) return Missing();
        if (!await CanView(request, ct)) return Forbidden();
        return Ok(await db.InspectionFindings.AsNoTracking()
            .Where(x => x.InspectionReport!.VesselId == request.VesselId &&
                x.InspectionReport.State == InspectionReportState.Finalized)
            .OrderByDescending(x => x.InspectionReport!.InspectionDate)
            .ThenBy(x => x.SequenceNumber).Select(x => new
            {
                x.Id, x.InspectionReport!.ReportNumber, x.InspectionReport.InspectionDate,
                FindingNumber = x.SequenceNumber, x.Department, x.Priority, x.Status,
                x.ExactLocation, x.Description, x.EquipmentOrArea
            }).ToListAsync(ct));
    }

    [HttpPost("{id:guid}/items")]
    public async Task<IActionResult> AddItems(Guid id,
        AddServiceRequestItems input, CancellationToken ct)
    {
        var request = await db.ServiceRequests.Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(request, ct); if (denied is not null) return denied;
        var next = request!.Items.Select(x => x.SequenceNumber).DefaultIfEmpty().Max() + 1;
        var now = DateTime.UtcNow; var added = new List<ServiceRequestItem>();
        if (input.ItemType == ServiceRequestItemType.Manual)
        {
            if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.ServiceDescription))
                return Invalid("Manual item title and service description are required.");
            added.Add(new ServiceRequestItem
            {
                Id = Guid.NewGuid(), ServiceRequestId = id, SequenceNumber = next,
                ItemType = input.ItemType, Title = input.Title.Trim(),
                Location = Clean(input.Location), Description = Clean(input.Description),
                ServiceDescription = input.ServiceDescription.Trim(), Remarks = Clean(input.Remarks),
                CreatedUtc = now, UpdatedUtc = now
            });
        }
        else
        {
            var ids = input.SourceIds?.Distinct().ToList() ?? [];
            if (ids.Count == 0) return Invalid("Select at least one source item.");
            if (input.ItemType == ServiceRequestItemType.Equipment)
            {
                var rows = await db.Equipment.AsNoTracking().Where(x =>
                    x.VesselId == request.VesselId && ids.Contains(x.Id)).ToListAsync(ct);
                if (rows.Count != ids.Count) return Invalid("Equipment must belong to the request vessel.");
                var duplicate = request.Items.Any(x => x.EquipmentId != null && ids.Contains(x.EquipmentId.Value));
                if (duplicate && !input.AllowDuplicate) return Conflict("Selected equipment is already in the request.");
                foreach (var x in rows) added.Add(new ServiceRequestItem
                {
                    Id = Guid.NewGuid(), ServiceRequestId = id, SequenceNumber = next++,
                    ItemType = input.ItemType, EquipmentId = x.Id, Title = x.EquipmentName,
                    Category = x.Category == EquipmentCategory.RadioAndNavigation ? "Radio & Navigation" : x.Category.ToString(),
                    Maker = x.Maker, Model = x.Model, SerialNumber = x.SerialNumber,
                    CurrentDueDate = x.ExpiryDate,
                    Description = x.Remarks, ServiceDescription = input.ServiceDescription.Trim(),
                    Remarks = Clean(input.Remarks), CreatedUtc = now, UpdatedUtc = now
                });
            }
            else
            {
                var rows = await db.InspectionFindings.AsNoTracking().Include(x => x.InspectionReport)
                    .Where(x => ids.Contains(x.Id) && x.InspectionReport!.VesselId == request.VesselId &&
                        x.InspectionReport.State == InspectionReportState.Finalized).ToListAsync(ct);
                if (rows.Count != ids.Count) return Invalid("Findings must be finalized and belong to the request vessel.");
                var duplicate = request.Items.Any(x => x.InspectionFindingId != null &&
                    ids.Contains(x.InspectionFindingId.Value));
                if (duplicate && !input.AllowDuplicate) return Conflict("Selected finding is already in the request.");
                foreach (var x in rows) added.Add(new ServiceRequestItem
                {
                    Id = Guid.NewGuid(), ServiceRequestId = id, SequenceNumber = next++,
                    ItemType = input.ItemType, InspectionFindingId = x.Id,
                    InspectionReportNumber = x.InspectionReport!.ReportNumber,
                    InspectionFindingSequence = x.SequenceNumber, Title = x.EquipmentOrArea,
                    Location = x.ExactLocation, Priority = x.Priority.ToString(),
                    Description = x.Description, ServiceDescription = input.ServiceDescription.Trim(),
                    Remarks = Clean(input.Remarks), CreatedUtc = now, UpdatedUtc = now
                });
            }
        }
        db.AddRange(added); request.UpdatedUtc = now; await db.SaveChangesAsync(ct);
        return Ok(added.Select(MapItem));
    }

    [HttpPost("{id:guid}/items/reorder")]
    public async Task<IActionResult> Reorder(Guid id,
        ReorderServiceRequestItems input, CancellationToken ct)
    {
        var request = await db.ServiceRequests.Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(request, ct); if (denied is not null) return denied;
        if (input.ItemIds.Count != request!.Items.Count ||
            input.ItemIds.Distinct().Count() != input.ItemIds.Count ||
            input.ItemIds.Except(request.Items.Select(x => x.Id)).Any())
            return Invalid("Item order must contain every request item exactly once.");
        foreach (var x in request.Items) x.SequenceNumber = -x.SequenceNumber;
        await db.SaveChangesAsync(ct);
        for (var i = 0; i < input.ItemIds.Count; i++)
            request.Items.Single(x => x.Id == input.ItemIds[i]).SequenceNumber = i + 1;
        request.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/items/refresh-equipment")]
    public async Task<IActionResult> RefreshEquipment(Guid id, CancellationToken ct)
    {
        var request = await db.ServiceRequests.Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        var denied = await Editable(request, ct); if (denied is not null) return denied;
        var equipmentIds = request!.Items.Where(x => x.EquipmentId != null)
            .Select(x => x.EquipmentId!.Value).ToList();
        var equipment = await db.Equipment.AsNoTracking().Where(x =>
            x.VesselId == request.VesselId && equipmentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var item in request.Items.Where(x => x.EquipmentId != null))
            if (equipment.TryGetValue(item.EquipmentId!.Value, out var source))
            {
                item.Title = source.EquipmentName; item.Category = source.Category.ToString();
                item.Maker = source.Maker; item.Model = source.Model;
                item.SerialNumber = source.SerialNumber; item.CurrentDueDate = source.ExpiryDate;
                item.Description = source.Remarks; item.UpdatedUtc = DateTime.UtcNow;
            }
        request.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/mark-ready")]
    public Task<IActionResult> Ready(Guid id, CancellationToken ct) =>
        Transition(id, ServiceRequestStatus.Draft, ServiceRequestStatus.Ready, null, ct);
    [HttpPost("{id:guid}/mark-sent")]
    public Task<IActionResult> Sent(Guid id, CancellationToken ct) =>
        Transition(id, ServiceRequestStatus.Ready, ServiceRequestStatus.Sent, null, ct);
    [HttpPost("{id:guid}/complete")]
    public Task<IActionResult> Complete(Guid id, CancellationToken ct) =>
        Transition(id, ServiceRequestStatus.Sent, ServiceRequestStatus.Completed, null, ct);
    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, ServiceRequestReason reason, CancellationToken ct) =>
        Transition(id, null, ServiceRequestStatus.Cancelled, reason.Reason, ct);

    [HttpPost("{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid id, ServiceRequestReason reason, CancellationToken ct)
    {
        if (!User.IsInRole("OfficeAdmin")) return Forbidden();
        if (string.IsNullOrWhiteSpace(reason.Reason)) return Invalid("A reopening reason is required.");
        var request = await db.ServiceRequests.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (request is null) return Missing();
        if (request.Status is not (ServiceRequestStatus.Sent or ServiceRequestStatus.Cancelled))
            return Conflict("Only Sent or Cancelled requests may be reopened.");
        request.Status = ServiceRequestStatus.Draft; request.ReopenedUtc = request.UpdatedUtc = DateTime.UtcNow;
        request.ReopenedBy = User.FindFirstValue(ClaimTypes.Name) ?? "Administrator";
        request.ReopenReason = reason.Reason.Trim(); await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/generate-pdf")]
    public async Task<IActionResult> GeneratePdf(Guid id, CancellationToken ct)
    {
        var request = await Load(id, true, ct); if (request is null) return Missing();
        if (!await CanView(request, ct)) return Forbidden();
        if (User.IsInRole("Crew") && request.CreatedByUserId != CurrentUserId())
            return Forbidden();
        byte[] bytes;
        try { bytes = pdfService.Generate(request); }
        catch { return Problem(statusCode: 500, title: "PDF generation failed"); }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var version = (await db.ServiceRequestPdfs.Where(x => x.ServiceRequestId == id)
            .Select(x => (int?)x.VersionNumber).MaxAsync(ct) ?? 0) + 1;
        var relative = await storage.SavePdfAsync(id, version, bytes, ct);
        try
        {
            await db.ServiceRequestPdfs.Where(x => x.ServiceRequestId == id && x.IsCurrent)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsCurrent, false), ct);
            var pdf = new ServiceRequestPdf
            {
                Id = Guid.NewGuid(), ServiceRequestId = id, VersionNumber = version,
                FileName = $"{request.RequestNumber}-v{version}.pdf", StoredFileName = relative,
                MimeType = "application/pdf", FileSize = bytes.LongLength,
                GeneratedUtc = DateTime.UtcNow,
                GeneratedBy = User.FindFirstValue(ClaimTypes.Name) ?? "User", IsCurrent = true
            };
            db.Add(pdf); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Ok(MapPdf(pdf));
        }
        catch { await transaction.RollbackAsync(ct); SafeDelete(relative); throw; }
    }

    [HttpGet("{id:guid}/pdf-versions")]
    public async Task<IActionResult> PdfVersions(Guid id, CancellationToken ct)
    {
        var request = await db.ServiceRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (request is null) return Missing(); if (!await CanView(request, ct)) return Forbidden();
        return Ok(await db.ServiceRequestPdfs.AsNoTracking().Where(x => x.ServiceRequestId == id)
            .OrderByDescending(x => x.VersionNumber).Select(x => new ServiceRequestPdfDto(
                x.Id, x.VersionNumber, x.FileName, x.FileSize, x.GeneratedUtc,
                x.GeneratedBy, x.IsCurrent)).ToListAsync(ct));
    }

    private async Task<IActionResult> Transition(Guid id, ServiceRequestStatus? from,
        ServiceRequestStatus to, string? reason, CancellationToken ct)
    {
        var request = await db.ServiceRequests.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (request is null) return Missing();
        if (!await CanEdit(request, ct) || User.IsInRole("Crew")) return Forbidden();
        if (from is not null && request.Status != from)
            return Conflict($"Request must be {from} before it can become {to}.");
        if (to == ServiceRequestStatus.Cancelled)
        {
            if (request.Status is ServiceRequestStatus.Completed or ServiceRequestStatus.Cancelled)
                return Conflict("This request cannot be cancelled.");
            if (string.IsNullOrWhiteSpace(reason)) return Invalid("A cancellation reason is required.");
            request.CancellationReason = reason.Trim(); request.CancelledUtc = DateTime.UtcNow;
        }
        if (to == ServiceRequestStatus.Sent) request.SentUtc = DateTime.UtcNow;
        if (to == ServiceRequestStatus.Completed) request.CompletedUtc = DateTime.UtcNow;
        request.Status = to; request.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
        if (to is ServiceRequestStatus.Sent or ServiceRequestStatus.Completed)
            await audit.WriteAsync(User,
                to == ServiceRequestStatus.Sent
                    ? "ServiceRequestSent" : "ServiceRequestCompleted",
                "ServiceRequest", request.Id.ToString(), request.RequestNumber, ct);
        return NoContent();
    }

    private async Task<ServiceRequest?> Load(Guid id, bool tracked, CancellationToken ct)
    {
        IQueryable<ServiceRequest> q = db.ServiceRequests.Include(x => x.Vessel)
            .Include(x => x.Items).Include(x => x.Attachments).Include(x => x.PdfVersions);
        if (!tracked) q = q.AsNoTracking();
        return await q.SingleOrDefaultAsync(x => x.Id == id, ct);
    }
    private async Task<IActionResult?> Editable(ServiceRequest? request, CancellationToken ct)
    {
        if (request is null) return Missing();
        if (!await CanEdit(request, ct)) return Forbidden();
        return request.Status is ServiceRequestStatus.Draft or ServiceRequestStatus.Ready
            ? null : Conflict("Sent, Completed, and Cancelled requests are read-only.");
    }
    internal async Task<bool> CanEdit(ServiceRequest request, CancellationToken ct)
    {
        if (!await CanAccess(request.VesselId, ct)) return false;
        if (!User.IsInRole("Crew")) return true;
        return request.Status == ServiceRequestStatus.Draft &&
            request.CreatedByUserId == CurrentUserId();
    }
    private async Task<bool> CanView(ServiceRequest request, CancellationToken ct)
    {
        if (!await CanAccess(request.VesselId, ct)) return false;
        if (!User.IsInRole("Crew")) return true;
        return request.CreatedByUserId == CurrentUserId() ||
            request.Status is ServiceRequestStatus.Sent or
                ServiceRequestStatus.Completed or ServiceRequestStatus.Cancelled;
    }
    private Task<bool> CanAccess(Guid vessel, CancellationToken ct) =>
        access.CanAccessVesselAsync(User, vessel, ct);
    private Guid CurrentUserId() => UserId(out var id) ? id : Guid.Empty;
    private bool UserId(out Guid id) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out id);
    private static bool ValidHeader(params string[] values) =>
        values.All(x => !string.IsNullOrWhiteSpace(x));
    private static string? Clean(string? x) => string.IsNullOrWhiteSpace(x) ? null : x.Trim();
    private static string VesselCode(string name)
    {
        var code = NonCode().Replace(name.ToUpperInvariant(), "");
        return string.IsNullOrWhiteSpace(code) ? "VESSEL" : code[..Math.Min(20, code.Length)];
    }
    [GeneratedRegex("[^A-Z0-9]+")] private static partial Regex NonCode();
    private ObjectResult Missing() => Problem(statusCode: 404, title: "Service request not found");
    private ObjectResult Forbidden() => Problem(statusCode: 403, title: "Access denied");
    private ObjectResult Invalid(string detail) => Problem(statusCode: 400, title: "Invalid request", detail: detail);
    private ObjectResult Conflict(string detail) => Problem(statusCode: 409, title: "Service request conflict", detail: detail);
    private void SafeDelete(string path) { try { storage.DeleteIfExists(path); } catch { } }
    internal static ServiceRequestItemDto MapItem(ServiceRequestItem x) => new(
        x.Id, x.SequenceNumber, x.ItemType, x.EquipmentId, x.InspectionFindingId,
        x.InspectionReportNumber, x.InspectionFindingSequence, x.Title, x.Category,
        x.Maker, x.Model, x.SerialNumber, x.Location, x.CurrentDueDate, x.Priority,
        x.Description, x.ServiceDescription, x.Remarks);
    private static ServiceRequestAttachmentDto MapAttachment(ServiceRequestAttachment x) =>
        new(x.Id, x.OriginalFileName, x.MimeType, x.FileSize, x.Description,
            x.CreatedUtc, x.CreatedBy);
    private static ServiceRequestPdfDto MapPdf(ServiceRequestPdf x) => new(
        x.Id, x.VersionNumber, x.FileName, x.FileSize, x.GeneratedUtc, x.GeneratedBy, x.IsCurrent);
    private static ManagedServiceRequestDto Map(ServiceRequest x) => new(
        x.Id, x.RequestNumber, x.VesselId, x.Vessel!.VesselName, x.Vessel.ImoNumber,
        x.RequestDate, x.RequestedBy, x.ContactName, x.ContactEmail, x.ContactPhone,
        x.PreferredPort, x.ExpectedArrivalDate, x.ExpectedDepartureDate, x.ServiceCompany,
        x.AttentionTo, x.Subject, x.GeneralRemarks, x.InvoicingDetails, x.Status,
        x.CreatedUtc, x.UpdatedUtc, x.SentUtc, x.CompletedUtc, x.CancelledUtc,
        x.CancellationReason, x.ReopenedUtc, x.ReopenedBy, x.ReopenReason,
        x.Items.OrderBy(i => i.SequenceNumber).Select(MapItem).ToList(),
        x.Attachments.OrderBy(a => a.CreatedUtc).Select(MapAttachment).ToList());
}
