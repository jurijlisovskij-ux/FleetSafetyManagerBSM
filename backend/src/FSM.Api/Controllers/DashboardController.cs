using FSM.Api.Authorization;
using FSM.Api.Synchronization;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace FSM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/dashboard")]
public sealed class DashboardController(
    FsmDbContext db,
    SynchronizationStatusService synchronizationStatuses,
    VesselAccessService vesselAccess) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var vessels = await vesselAccess.GetAccessibleVesselsAsync(
            User,
            cancellationToken);
        if (vessels is null)
            return Unauthorized();

        var vesselIds = vessels
            .Select(x => x.Id)
            .ToList();

        var synchronization = await synchronizationStatuses.GetAsync(
            vessels,
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

        var technicalSuperintendents = new List<object>();
        if (string.Equals(
            User.FindFirstValue(ClaimTypes.Role),
            UserRole.OfficeAdmin.ToString(),
            StringComparison.OrdinalIgnoreCase))
        {
            var superintendents = await db.Users.AsNoTracking()
                .Where(x => x.Role == UserRole.Superintendent)
                .OrderBy(x => x.FullName)
                .ThenBy(x => x.Username)
                .Select(x => new
                {
                    x.Id,
                    x.FullName,
                    VesselIds = x.VesselAssignments
                        .Select(a => a.VesselId)
                        .ToList()
                })
                .ToListAsync(cancellationToken);

            foreach (var superintendent in superintendents)
            {
                var assignedIds = superintendent.VesselIds.ToHashSet();
                var assignedStatuses = statuses
                    .Where(x => assignedIds.Contains(x.Item.VesselId))
                    .ToList();
                technicalSuperintendents.Add(new
                {
                    superintendent.Id,
                    superintendent.FullName,
                    VesselCount = assignedIds.Count,
                    Soon = assignedStatuses.Count(x =>
                        x.Status == EquipmentStatus.Soon),
                    Overdue = assignedStatuses.Count(x =>
                        x.Status == EquipmentStatus.Overdue),
                    Vessels = vessels
                        .Where(x => assignedIds.Contains(x.Id))
                        .Select(x => new
                        {
                            x.Id,
                            x.VesselName,
                            x.ImoNumber
                        })
                        .ToList()
                });
            }
        }

        return Ok(new
        {
            Vessels = vessels.Count,
            Equipment = equipment.Count,
            Ok = statuses.Count(x => x.Status == EquipmentStatus.OK),
            Soon = statuses.Count(x => x.Status == EquipmentStatus.Soon),
            Overdue = statuses.Count(x => x.Status == EquipmentStatus.Overdue),
            VesselRows = vesselRows,
            Upcoming = upcoming,
            TechnicalSuperintendents = technicalSuperintendents,
            Synchronization = synchronization
        });
    }
}
