using System.Security.Claims;
using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Services;

public sealed class EquipmentDocumentAccessService(FsmDbContext db)
{
    public async Task<bool> CanAccessVesselAsync(
        ClaimsPrincipal user,
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        if (user.IsInRole("OfficeAdmin"))
            return true;

        var userIdText = user.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(userIdText, out var userId))
            return false;

        return await db.UserVessels
            .AsNoTracking()
            .AnyAsync(
                assignment =>
                    assignment.UserId == userId &&
                    assignment.VesselId == vesselId,
                cancellationToken);
    }
}
