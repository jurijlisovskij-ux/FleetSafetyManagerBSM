using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using FSM.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api;

public static class SeedData
{
    public static async Task InitializeAsync(
        FsmDbContext db,
        IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("DevelopmentSeed:Enabled"))
            return;

        var administratorPassword = GetRequiredPassword(
            configuration,
            "AdministratorPassword");
        var superintendentPassword = GetRequiredPassword(
            configuration,
            "SuperintendentPassword");
        var crewPassword = GetRequiredPassword(
            configuration,
            "CrewPassword");

        if (await db.Users.AnyAsync()) return;

        var admin = new User
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            FullName = "Office Administrator",
            Email = "admin@fsm.local",
            PasswordHash = PasswordHasher.Hash(administratorPassword),
            Role = UserRole.OfficeAdmin
        };

        var superintendent = new User
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            FullName = "Technical Superintendent",
            Email = "superintendent@fsm.local",
            PasswordHash = PasswordHasher.Hash(superintendentPassword),
            Role = UserRole.Superintendent
        };

        var crew = new User
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            FullName = "Vessel Crew",
            Email = "crew@fsm.local",
            PasswordHash = PasswordHasher.Hash(crewPassword),
            Role = UserRole.Crew
        };

        var vessel = new Vessel
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            VesselName = "MV BALTIC",
            ImoNumber = "9126998",
            Flag = "Cyprus",
            CompanyName = "ABC Shipping Ltd.",
            InvoicingDetails =
                "ABC Shipping Ltd.\n10 Marine Street\nHamburg, Germany",
            AccountingEmail = "accounts@example.com"
        };

        db.Users.AddRange(admin, superintendent, crew);
        db.Vessels.Add(vessel);
        db.UserVessels.AddRange(
            new UserVessel { UserId = superintendent.Id, VesselId = vessel.Id },
            new UserVessel { UserId = crew.Id, VesselId = vessel.Id });

        db.Equipment.AddRange(
            new Equipment
            {
                Id = Guid.NewGuid(),
                VesselId = vessel.Id,
                Category = EquipmentCategory.LSA,
                EquipmentName = "Liferaft Port",
                Maker = "Viking",
                Model = "DK+",
                SerialNumber = "VK-PORT-001",
                Quantity = 1,
                InspectionBy = InspectionBy.ServiceCompany,
                ExpiryDate = new DateOnly(2026, 8, 15),
                Remarks = "Annual service."
            },
            new Equipment
            {
                Id = Guid.NewGuid(),
                VesselId = vessel.Id,
                Category = EquipmentCategory.LSA,
                EquipmentName = "EPIRB",
                Maker = "Jotron",
                Model = "Tron 60S",
                SerialNumber = "EP-9126998",
                Quantity = 1,
                InspectionBy = InspectionBy.ServiceCompany,
                ExpiryDate = new DateOnly(2026, 7, 1)
            },
            new Equipment
            {
                Id = Guid.NewGuid(),
                VesselId = vessel.Id,
                Category = EquipmentCategory.FFE,
                EquipmentName = "Fire Extinguisher CO2 5 kg",
                Maker = "Gloria",
                Model = "KS5SE",
                Quantity = 12,
                InspectionBy = InspectionBy.ServiceCompany,
                ExpiryDate = new DateOnly(2027, 1, 10)
            },
            new Equipment
            {
                Id = Guid.NewGuid(),
                VesselId = vessel.Id,
                Category = EquipmentCategory.FFE,
                EquipmentName = "EEBD",
                Maker = "Dräger",
                Model = "Oxy 3000",
                Quantity = 18,
                InspectionBy = InspectionBy.Crew,
                ExpiryDate = null
            },
            new Equipment
            {
                Id = Guid.NewGuid(),
                VesselId = vessel.Id,
                Category = EquipmentCategory.Medicine,
                EquipmentName = "Medical Oxygen Cylinder",
                Maker = "Linde",
                Model = "5 L",
                Quantity = 2,
                InspectionBy = InspectionBy.ServiceCompany,
                ExpiryDate = new DateOnly(2026, 9, 20)
            },
            new Equipment
            {
                Id = Guid.NewGuid(),
                VesselId = vessel.Id,
                Category = EquipmentCategory.RadioAndNavigation,
                EquipmentName = "Portable VHF",
                Maker = "Entel",
                Model = "HT649",
                SerialNumber = "EN-48112",
                Quantity = 3,
                InspectionBy = InspectionBy.ServiceCompany,
                ExpiryDate = new DateOnly(2026, 12, 31)
            });

        await db.SaveChangesAsync();
    }

    private static string GetRequiredPassword(
        IConfiguration configuration,
        string name)
    {
        var value = configuration[$"DevelopmentSeed:{name}"];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Development seeding is enabled, but " +
                $"DevelopmentSeed:{name} is missing.");
        }

        return value;
    }
}
