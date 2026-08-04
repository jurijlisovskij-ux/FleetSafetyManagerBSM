using System.Security.Claims;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Authorization;

public sealed class VesselAccessService(
    FsmDbContext db,
    IConfiguration configuration)
{
    private readonly bool isShipEdition = string.Equals(
        configuration["Application:Edition"],
        "Ship",
        StringComparison.OrdinalIgnoreCase);

    private readonly Guid? configuredShipVesselId = Guid.TryParse(
        configuration["Application:ShipVesselId"],
        out var vesselId)
            ? vesselId
            : null;

    public async Task<IReadOnlyList<Vessel>?> GetAccessibleVesselsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var role = user.FindFirstValue(ClaimTypes.Role);
        var query = db.Vessels.AsNoTracking();

        // Office administrators are never assignment-scoped. Check the role
        // before parsing an identifier so an otherwise valid administrator
        // token cannot accidentally fall through to an empty assignment list.
        if (!isShipEdition &&
            string.Equals(role, "OfficeAdmin", StringComparison.OrdinalIgnoreCase))
        {
            return await query
                .OrderBy(x => x.VesselName)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        var userIdText = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdText, out var userId))
            return null;

        if (isShipEdition &&
            string.Equals(role, "Crew", StringComparison.OrdinalIgnoreCase))
        {
            if (!configuredShipVesselId.HasValue)
                return Array.Empty<Vessel>();

            return await query
                .Where(vessel => vessel.Id == configuredShipVesselId.Value)
                .OrderBy(x => x.VesselName)
                .ThenBy(x => x.Id)
                .Take(1)
                .ToListAsync(cancellationToken);
        }

        query = query.Where(vessel =>
            db.UserVessels.Any(assignment =>
                assignment.UserId == userId &&
                assignment.VesselId == vessel.Id));

        return await query
            .OrderBy(x => x.VesselName)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>?> GetAccessibleVesselIdsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var vessels = await GetAccessibleVesselsAsync(
            user,
            cancellationToken);

        return vessels?
            .Select(x => x.Id)
            .ToList();
    }

    public async Task<bool> CanAccessVesselAsync(
        ClaimsPrincipal user,
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var accessibleVesselIds = await GetAccessibleVesselIdsAsync(
            user,
            cancellationToken);

        return accessibleVesselIds?.Contains(vesselId) == true;
    }
}
