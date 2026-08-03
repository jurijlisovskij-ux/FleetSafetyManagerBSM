using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using FSM.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api;

public static class SeedData
{
    public static async Task InitializeAsync(FsmDbContext db)
    {
        if (await db.Users.AnyAsync()) return;

        var admin = new User
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            FullName = "Office Administrator",
            Email = "admin@fsm.local",
            PasswordHash = PasswordHasher.Hash("Admin123!"),
            Role = UserRole.OfficeAdmin
        };

        var superintendent = new User
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            FullName = "Technical Superintendent",
            Email = "superintendent@fsm.local",
            PasswordHash = PasswordHasher.Hash("Super123!"),
            Role = UserRole.Superintendent
        };

        var crew = new User
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            FullName = "Vessel Crew",
            Email = "crew@fsm.local",
            PasswordHash = PasswordHasher.Hash("Crew123!"),
            Role = UserRole.Crew
        };

        db.Users.AddRange(admin, superintendent, crew);

        await db.SaveChangesAsync();
    }
}
