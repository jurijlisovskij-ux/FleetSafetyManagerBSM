using System.Security.Claims;
using FSM.Api.Synchronization;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/dashboard")]
public sealed class DashboardController(
    FsmDbContext db,
    SynchronizationStatusService synchronizationStatuses) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var userIdText = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = User.FindFirstValue(ClaimTypes.Role);

        if (!Guid.TryParse(userIdText, out var userId))
            return Unauthorized();

        var vesselQuery = db.Vessels.AsNoTracking();

        if (!string.Equals(role, "OfficeAdmin", StringComparison.Ordinal))
        {
            vesselQuery = vesselQuery.Where(vessel =>
                db.UserVessels.Any(assignment =>
                    assignment.UserId == userId &&
                    assignment.VesselId == vessel.Id));
        }

        var vessels = await vesselQuery
            .OrderBy(x => x.VesselName)
            .ToListAsync(cancellationToken);

        var vesselIds = vessels.Select(x => x.Id).ToList();
        var synchronization = await synchronizationStatuses.GetAsync(
            vesselIds,
            cancellationToken);
        var equipment = await db.Equipment.AsNoTracking()
            .Where(x => vesselIds.Contains(x.VesselId))
            .ToListAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        const int reminderDays = 60;

        var statuses = equipment
            .Select(item => new
            {
                Item = item,
                Status = item.CalculateStatus(today, reminderDays)
            })
            .ToList();

        var vesselRows = vessels.Select(vessel =>
        {
            var items = statuses
                .Where(x => x.Item.VesselId == vessel.Id)
                .ToList();

            return new
            {
                vessel.Id,
                vessel.VesselName,
                vessel.ImoNumber,
                vessel.Flag,
                Equipment = items.Count,
                Ok = items.Count(x => x.Status == EquipmentStatus.OK),
                Soon = items.Count(x => x.Status == EquipmentStatus.Soon),
                Overdue = items.Count(x => x.Status == EquipmentStatus.Overdue)
            };
        }).ToList();

        var vesselLookup = vessels.ToDictionary(x => x.Id);

        var upcoming = statuses
            .Where(x => x.Item.ExpiryDate != null)
            .OrderBy(x => x.Item.ExpiryDate)
            .ThenBy(x => x.Item.EquipmentName)
            .Take(20)
            .Select(x => new
            {
                x.Item.Id,
                x.Item.VesselId,
                VesselName = vesselLookup[x.Item.VesselId].VesselName,
                ImoNumber = vesselLookup[x.Item.VesselId].ImoNumber,
                x.Item.EquipmentName,
                Category = x.Item.Category == EquipmentCategory.RadioAndNavigation
                    ? "Radio & Navigation"
                    : x.Item.Category.ToString(),
                x.Item.ExpiryDate,
                Status = x.Status.ToString(),
                DaysLeft = x.Item.ExpiryDate!.Value.DayNumber - today.DayNumber
            })
            .ToList();

        return Ok(new
        {
            Vessels = vessels.Count,
            Equipment = equipment.Count,
            Ok = statuses.Count(x => x.Status == EquipmentStatus.OK),
            Soon = statuses.Count(x => x.Status == EquipmentStatus.Soon),
            Overdue = statuses.Count(x => x.Status == EquipmentStatus.Overdue),
            VesselRows = vesselRows,
            Upcoming = upcoming,
            Synchronization = synchronization
        });
    }
}
