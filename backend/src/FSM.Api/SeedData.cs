using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using FSM.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api;

public static class SeedData
{
    private const string DefaultCrewUsername = "crew@fsm.local";

    public static async Task InitializeAsync(FsmDbContext db)
    {
        if (await db.Users.AnyAsync()) return;

        var admin = new User
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            FullName = "Office Administrator",
            Username = "admin@fsm.local",
            Email = "admin@fsm.local",
            PasswordHash = PasswordHasher.Hash("Admin123!"),
            Role = UserRole.OfficeAdmin
        };

        var superintendent = new User
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            FullName = "Technical Superintendent",
            Username = "superintendent@fsm.local",
            Email = "superintendent@fsm.local",
            PasswordHash = PasswordHasher.Hash("Super123!"),
            Role = UserRole.Superintendent
        };

        var crew = new User
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            FullName = "Vessel Crew",
            Username = "crew@fsm.local",
            Email = "crew@fsm.local",
            PasswordHash = PasswordHasher.Hash("Crew123!"),
            Role = UserRole.Crew
        };

        db.Users.AddRange(admin, superintendent, crew);

        await db.SaveChangesAsync();
    }

    public static async Task EnsureEditionAccountsAsync(
        FsmDbContext db,
        string edition,
        string? shipVesselId,
        string? shipVesselName,
        string? shipImoNumber,
        CancellationToken cancellationToken = default)
    {
        if (!edition.Equals("Ship", StringComparison.OrdinalIgnoreCase)) return;

        var crew = await db.Users
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                x => x.Username.ToLower() == DefaultCrewUsername,
                cancellationToken);

        if (crew is null)
        {
            var defaultId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            if (await db.Users.IgnoreQueryFilters()
                .AnyAsync(x => x.Id == defaultId, cancellationToken))
            {
                defaultId = Guid.NewGuid();
            }

            crew = new User
            {
                Id = defaultId,
                FullName = "Vessel Crew",
                Username = DefaultCrewUsername,
                Email = DefaultCrewUsername,
                PasswordHash = PasswordHasher.Hash("Crew123!"),
                Role = UserRole.Crew
            };
            db.Users.Add(crew);
        }
        else
        {
            // Older Ship databases may contain the default crew account with a
            // legacy numeric role value. Preserve its administrator-managed
            // password, but repair the edition-specific access attributes.
            crew.Role = UserRole.Crew;
            crew.IsActive = true;
            crew.IsDeleted = false;
            crew.IsLocked = false;
            crew.FailedLoginAttempts = 0;
        }

        if (Guid.TryParse(shipVesselId, out var configuredVesselId) &&
            !string.IsNullOrWhiteSpace(shipVesselName) &&
            !string.IsNullOrWhiteSpace(shipImoNumber))
        {
            var normalizedImo = shipImoNumber.Trim();
            var vessel = await db.Vessels
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    x => x.Id == configuredVesselId,
                    cancellationToken);

            vessel ??= await db.Vessels
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    x => x.ImoNumber == normalizedImo,
                    cancellationToken);

            if (vessel is null)
            {
                vessel = new Vessel
                {
                    Id = configuredVesselId,
                    VesselName = shipVesselName.Trim(),
                    ImoNumber = normalizedImo
                };
                db.Vessels.Add(vessel);
            }
            else
            {
                vessel.VesselName = shipVesselName.Trim();
                vessel.ImoNumber = normalizedImo;
                vessel.IsDeleted = false;
            }

            var assignment = await db.UserVessels
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    x => x.UserId == crew.Id && x.VesselId == vessel.Id,
                    cancellationToken);
            if (assignment is null)
            {
                db.UserVessels.Add(new UserVessel
                {
                    UserId = crew.Id,
                    VesselId = vessel.Id
                });
            }
            else
            {
                assignment.IsDeleted = false;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
