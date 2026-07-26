using System.Security.Claims;
using FSM.Api.Models;
using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController, Authorize, Route("api/fleet-dashboard")]
public sealed class FleetDashboardController(
    FsmDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        Guid? vesselId, Guid? superintendentId, string? status,
        string? category, int? year, string? search, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Problem(statusCode: 401, title: "Authentication required");
        if (year is < 2000 or > 9999)
            return Problem(statusCode: 400, title: "Invalid year");
        if (!string.IsNullOrWhiteSpace(status) &&
            !new[] { "Green", "Yellow", "Red" }.Contains(
                status, StringComparer.OrdinalIgnoreCase))
            return Problem(statusCode: 400, title: "Invalid status");

        var vesselQuery = db.Vessels.AsNoTracking();
        if (!User.IsInRole("OfficeAdmin"))
            vesselQuery = vesselQuery.Where(v => db.UserVessels.Any(a =>
                a.UserId == userId && a.VesselId == v.Id));
        if (vesselId is not null)
            vesselQuery = vesselQuery.Where(v => v.Id == vesselId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            vesselQuery = vesselQuery.Where(v =>
                EF.Functions.Like(v.VesselName, $"%{term}%") ||
                EF.Functions.Like(v.ImoNumber, $"%{term}%"));
        }
        if (superintendentId is not null)
        {
            if (!User.IsInRole("OfficeAdmin") && superintendentId != userId)
                return Problem(statusCode: 403, title: "Access denied");
            vesselQuery = vesselQuery.Where(v => db.UserVessels.Any(a =>
                a.UserId == superintendentId && a.VesselId == v.Id));
        }

        var vessels = await vesselQuery.OrderBy(v => v.VesselName)
            .Select(v => new { v.Id, v.VesselName, v.ImoNumber }).ToListAsync(ct);
        var ids = vessels.Select(v => v.Id).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var soonDate = today.AddDays(60);

        EquipmentCategory? selectedCategory = null;
        var equipmentQuery = db.Equipment.AsNoTracking().Where(x => ids.Contains(x.VesselId));
        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryValue = category.Replace(" & ", "And", StringComparison.Ordinal);
            if (!Enum.TryParse<EquipmentCategory>(categoryValue, true, out var parsed))
                return Problem(statusCode: 400, title: "Invalid equipment category");
            selectedCategory = parsed;
            equipmentQuery = equipmentQuery.Where(x => x.Category == parsed);
        }
        var equipment = await equipmentQuery.GroupBy(x => x.VesselId).Select(g => new
        {
            VesselId = g.Key, Total = g.Count(),
            Overdue = g.Count(x => x.ExpiryDate != null && x.ExpiryDate < today),
            Soon = g.Count(x => x.ExpiryDate != null && x.ExpiryDate >= today &&
                x.ExpiryDate <= soonDate)
        }).ToListAsync(ct);

        var reportQuery = db.InspectionReports.AsNoTracking().Where(x => ids.Contains(x.VesselId));
        if (year is not null) reportQuery = reportQuery.Where(x => x.InspectionDate.Year == year);
        var reports = await reportQuery.GroupBy(x => x.VesselId).Select(g => new
        {
            VesselId = g.Key,
            Draft = g.Count(x => x.State == InspectionReportState.Draft),
            Finalized = g.Count(x => x.State == InspectionReportState.Finalized)
        }).ToListAsync(ct);

        var findingQuery = db.InspectionFindings.AsNoTracking()
            .Where(x => ids.Contains(x.InspectionReport!.VesselId) &&
                x.Status == InspectionFindingStatus.Open);
        if (year is not null) findingQuery = findingQuery.Where(x =>
            x.InspectionReport!.InspectionDate.Year == year);
        var findings = await findingQuery.GroupBy(x => x.InspectionReport!.VesselId)
            .Select(g => new
            {
                VesselId = g.Key,
                Critical = g.Count(x => x.Priority == InspectionPriority.Critical),
                Urgent = g.Count(x => x.Priority == InspectionPriority.Urgent),
                Normal = g.Count(x => x.Priority == InspectionPriority.Normal)
            }).ToListAsync(ct);

        var serviceQuery = db.ServiceRequests.AsNoTracking().Where(x => ids.Contains(x.VesselId));
        if (year is not null) serviceQuery = serviceQuery.Where(x => x.RequestDate.Year == year);
        var services = await serviceQuery.GroupBy(x => x.VesselId).Select(g => new
        {
            VesselId = g.Key,
            Draft = g.Count(x => x.Status == ServiceRequestStatus.Draft),
            Ready = g.Count(x => x.Status == ServiceRequestStatus.Ready),
            Sent = g.Count(x => x.Status == ServiceRequestStatus.Sent),
            Completed = g.Count(x => x.Status == ServiceRequestStatus.Completed),
            Cancelled = g.Count(x => x.Status == ServiceRequestStatus.Cancelled)
        }).ToListAsync(ct);

        var equipmentMap = equipment.ToDictionary(x => x.VesselId);
        var reportMap = reports.ToDictionary(x => x.VesselId);
        var findingMap = findings.ToDictionary(x => x.VesselId);
        var serviceMap = services.ToDictionary(x => x.VesselId);
        var rows = vessels.Select(v =>
        {
            equipmentMap.TryGetValue(v.Id, out var e); reportMap.TryGetValue(v.Id, out var r);
            findingMap.TryGetValue(v.Id, out var f); serviceMap.TryGetValue(v.Id, out var s);
            var overdue = e?.Overdue ?? 0; var critical = f?.Critical ?? 0;
            var open = critical + (f?.Urgent ?? 0) + (f?.Normal ?? 0);
            var drafts = r?.Draft ?? 0; var active = (s?.Draft ?? 0) + (s?.Ready ?? 0) + (s?.Sent ?? 0);
            var overall = overdue > 0 || critical > 0 ? "Red"
                : open > 0 || drafts > 0 || active > 0 ? "Yellow" : "Green";
            return new FleetDashboardVesselDto(v.Id, v.VesselName, v.ImoNumber,
                overdue, critical, open, drafts, active, null, overall);
        }).Where(x => string.IsNullOrWhiteSpace(status) ||
            x.OverallStatus.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();

        var visibleIds = rows.Select(x => x.VesselId).ToHashSet();
        var notifications = await Notifications(ids, vessels.ToDictionary(x => x.Id, x => x.VesselName),
            today, year, selectedCategory, ct);
        notifications = notifications.Where(x => visibleIds.Contains(x.VesselId))
            .OrderByDescending(x => x.EventUtc).Take(100).ToList();

        var counts = new FleetDashboardCountsDto(
            equipment.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Total),
            equipment.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Total - x.Overdue - x.Soon),
            equipment.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Soon),
            equipment.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Overdue),
            reports.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Draft),
            reports.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Finalized),
            findings.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Critical),
            findings.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Urgent),
            findings.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Normal),
            services.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Draft),
            services.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Ready),
            services.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Sent),
            services.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Completed),
            services.Where(x => visibleIds.Contains(x.VesselId)).Sum(x => x.Cancelled));

        var charts = new[]
        {
            Chart("Equipment", ("OK", counts.EquipmentOk), ("Soon", counts.EquipmentSoon), ("Overdue", counts.EquipmentOverdue)),
            Chart("Findings", ("Critical", counts.CriticalOpen), ("Urgent", counts.UrgentOpen), ("Normal", counts.NormalOpen)),
            Chart("Service Requests", ("Draft", counts.ServiceDraft), ("Ready", counts.ServiceReady), ("Sent", counts.ServiceSent), ("Completed", counts.ServiceCompleted)),
            Chart("Inspection Reports", ("Draft", counts.InspectionDraft), ("Finalized", counts.InspectionFinalized))
        };
        var vesselOptions = await AuthorizedVessels(userId, ct);
        var superintendentOptions = User.IsInRole("OfficeAdmin")
            ? await db.Users.AsNoTracking().Where(x => x.Role == UserRole.Superintendent && x.IsActive)
                .OrderBy(x => x.FullName).Select(x => new FleetDashboardFilterOptionDto(x.Id, x.FullName)).ToListAsync(ct)
            : [];
        return Ok(new FleetDashboardDto(counts, rows, charts, notifications,
            vesselOptions, superintendentOptions));
    }

    [HttpGet("vessels/{vesselId:guid}")]
    public async Task<IActionResult> GetVessel(Guid vesselId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Problem(statusCode: 401, title: "Authentication required");
        var allowed = User.IsInRole("OfficeAdmin") || await db.UserVessels.AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.VesselId == vesselId, ct);
        if (!allowed) return Problem(statusCode: 403, title: "Access denied");
        if (!await db.Vessels.AsNoTracking().AnyAsync(x => x.Id == vesselId, ct))
            return Problem(statusCode: 404, title: "Vessel not found");
        return await Get(vesselId, null, null, null, null, null, ct);
    }

    private async Task<List<FleetNotificationDto>> Notifications(
        List<Guid> ids, Dictionary<Guid, string> names, DateOnly today, int? year,
        EquipmentCategory? category, CancellationToken ct)
    {
        var result = new List<FleetNotificationDto>();
        var due = today.AddDays(30);
        var equipment = await db.Equipment.AsNoTracking().Where(x =>
            ids.Contains(x.VesselId) && x.ExpiryDate != null && x.ExpiryDate <= due &&
            (category == null || x.Category == category))
            .Select(x => new { x.VesselId, x.EquipmentName, x.ExpiryDate }).ToListAsync(ct);
        result.AddRange(equipment.Select(x => new FleetNotificationDto("Equipment",
            x.ExpiryDate < today ? "Red" : "Yellow", x.VesselId, names[x.VesselId],
            x.ExpiryDate < today ? "Equipment overdue" : "Equipment expires within 30 days",
            $"{x.EquipmentName} · {x.ExpiryDate:dd MMM yyyy}",
            x.ExpiryDate!.Value.ToDateTime(TimeOnly.MinValue))));
        var draftDays = configuration.GetValue<int?>("FleetDashboard:DraftInspectionAgeDays") ?? 14;
        var draftCutoff = DateTime.UtcNow.AddDays(-draftDays);
        var drafts = await db.InspectionReports.AsNoTracking().Where(x =>
            ids.Contains(x.VesselId) && x.State == InspectionReportState.Draft &&
            x.CreatedUtc <= draftCutoff && (year == null || x.InspectionDate.Year == year))
            .Select(x => new { x.VesselId, x.ReportNumber, x.CreatedUtc }).ToListAsync(ct);
        result.AddRange(drafts.Select(x => new FleetNotificationDto("Inspection", "Yellow",
            x.VesselId, names[x.VesselId], "Inspection draft requires attention",
            x.ReportNumber, x.CreatedUtc)));
        var critical = await db.InspectionFindings.AsNoTracking().Where(x =>
            ids.Contains(x.InspectionReport!.VesselId) &&
            x.Priority == InspectionPriority.Critical &&
            x.Status == InspectionFindingStatus.Open &&
            (year == null || x.InspectionReport!.InspectionDate.Year == year))
            .Select(x => new { x.InspectionReport!.VesselId, x.EquipmentOrArea,
                x.InspectionReport.ReportNumber, x.UpdatedUtc }).ToListAsync(ct);
        result.AddRange(critical.Select(x => new FleetNotificationDto("Inspection", "Red",
            x.VesselId, names[x.VesselId], "Critical finding remains open",
            $"{x.ReportNumber} · {x.EquipmentOrArea}", x.UpdatedUtc)));
        var service = await db.ServiceRequests.AsNoTracking().Where(x => ids.Contains(x.VesselId) &&
            (x.Status == ServiceRequestStatus.Ready || x.Status == ServiceRequestStatus.Sent) &&
            (year == null || x.RequestDate.Year == year))
            .Select(x => new { x.VesselId, x.RequestNumber, x.Status, x.UpdatedUtc }).ToListAsync(ct);
        result.AddRange(service.Select(x => new FleetNotificationDto("Service Request", "Yellow",
            x.VesselId, names[x.VesselId],
            x.Status == ServiceRequestStatus.Ready ? "Ready request not sent" : "Sent request not completed",
            x.RequestNumber, x.UpdatedUtc)));
        return result;
    }

    private async Task<List<FleetDashboardFilterOptionDto>> AuthorizedVessels(Guid userId, CancellationToken ct)
    {
        var q = db.Vessels.AsNoTracking();
        if (!User.IsInRole("OfficeAdmin"))
            q = q.Where(v => db.UserVessels.Any(a => a.UserId == userId && a.VesselId == v.Id));
        return await q.OrderBy(x => x.VesselName)
            .Select(x => new FleetDashboardFilterOptionDto(x.Id, x.VesselName)).ToListAsync(ct);
    }
    private static FleetDashboardChartDto Chart(string name,
        params (string Label, int Value)[] values) =>
        new(name, values.Select(x => new FleetDashboardChartValueDto(x.Label, x.Value)).ToList());
}
