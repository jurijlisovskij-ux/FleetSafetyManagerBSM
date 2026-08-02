using System.Security.Claims;
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

    public async Task<IReadOnlyList<Guid>?> GetAccessibleVesselIdsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var userIdText = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdText, out var userId))
            return null;

        var role = user.FindFirstValue(ClaimTypes.Role);
        var query = db.Vessels.AsNoTracking();

        if (string.Equals(role, "OfficeAdmin", StringComparison.Ordinal))
        {
            return await query
                .OrderBy(x => x.VesselName)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        query = query.Where(vessel =>
            db.UserVessels.Any(assignment =>
                assignment.UserId == userId &&
                assignment.VesselId == vessel.Id));

        if (isShipEdition &&
            string.Equals(role, "Crew", StringComparison.Ordinal))
        {
            if (configuredShipVesselId.HasValue)
            {
                query = query.Where(
                    vessel => vessel.Id == configuredShipVesselId.Value);
            }

            return await query
                .OrderBy(x => x.VesselName)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .Take(1)
                .ToListAsync(cancellationToken);
        }

        return await query
            .OrderBy(x => x.VesselName)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
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
