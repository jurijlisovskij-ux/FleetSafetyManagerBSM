using System.Net.Http.Headers;
using FSM.Api.Auth;
using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSM.Api.IntegrationTests;

public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public static readonly Guid AssignedVesselId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid OtherVesselId =
        Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid AssignedEquipmentId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid OtherEquipmentId =
        Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid AdminId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    public static readonly Guid SuperintendentId =
        Guid.Parse("30000000-0000-0000-0000-000000000002");
    public static readonly Guid CrewId =
        Guid.Parse("30000000-0000-0000-0000-000000000003");

    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "fsm-api-tests",
        Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(testRoot, "fsm-tests.db");
    public string StoragePath => Path.Combine(testRoot, "documents");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(testRoot);
        Directory.CreateDirectory(StoragePath);

        builder.UseEnvironment("Production");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    $"Data Source={DatabasePath}",
                ["Storage:RootPath"] = StoragePath,
                ["Jwt:Key"] =
                    "integration-test-signing-key-32-characters-minimum",
                ["DevelopmentSeed:Enabled"] = "false",
                ["Notifications:Enabled"] = "false"
            });
        });
    }

    public async Task ResetAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsmDbContext>();

        await db.InspectionReportPdfs.ExecuteDeleteAsync();
        await db.InspectionPhotos.ExecuteDeleteAsync();
        await db.InspectionFindings.ExecuteDeleteAsync();
        await db.InspectionReports.ExecuteDeleteAsync();
        await db.EquipmentDocuments.ExecuteDeleteAsync();
        await db.UserVessels.ExecuteDeleteAsync();
        await db.Equipment.ExecuteDeleteAsync();
        await db.Users.ExecuteDeleteAsync();
        await db.Vessels.ExecuteDeleteAsync();

        if (Directory.Exists(StoragePath))
            Directory.Delete(StoragePath, recursive: true);
        Directory.CreateDirectory(StoragePath);

        var assignedVessel = new Vessel
        {
            Id = AssignedVesselId,
            VesselName = "MV Assigned",
            ImoNumber = "9000001"
        };
        var otherVessel = new Vessel
        {
            Id = OtherVesselId,
            VesselName = "MV Other",
            ImoNumber = "9000002"
        };

        var admin = CreateUser(AdminId, "Admin", UserRole.OfficeAdmin);
        var superintendent = CreateUser(
            SuperintendentId,
            "Superintendent",
            UserRole.Superintendent);
        var crew = CreateUser(CrewId, "Crew", UserRole.Crew);

        db.AddRange(assignedVessel, otherVessel, admin, superintendent, crew);
        db.UserVessels.AddRange(
            new UserVessel
            {
                UserId = SuperintendentId,
                VesselId = AssignedVesselId
            },
            new UserVessel
            {
                UserId = CrewId,
                VesselId = AssignedVesselId
            });
        db.Equipment.AddRange(
            CreateEquipment(AssignedEquipmentId, AssignedVesselId, "EPIRB"),
            CreateEquipment(OtherEquipmentId, OtherVesselId, "Other EPIRB"));
        await db.SaveChangesAsync();
    }

    public HttpClient CreateAuthenticatedClient(Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsmDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
        var user = db.Users.Single(x => x.Id == userId);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.Create(user));
        return client;
    }

    public async Task<EquipmentDocument> AddDocumentAsync(
        Guid equipmentId,
        EquipmentDocumentType type,
        bool isCurrent,
        string storedFileName,
        string fileName = "document.pdf",
        DateTime? archivedUtc = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsmDbContext>();
        var document = new EquipmentDocument
        {
            Id = Guid.NewGuid(),
            EquipmentId = equipmentId,
            DocumentType = type,
            FileName = fileName,
            StoredFileName = storedFileName,
            MimeType = "application/pdf",
            FileSize = 14,
            IssueDate = new DateOnly(2025, 1, 2),
            ExpiryDate = new DateOnly(2026, 1, 2),
            DocumentNumber = "DOC-1",
            Issuer = "Test Issuer",
            Remarks = "Test remarks",
            IsCurrent = isCurrent,
            CreatedUtc = DateTime.UtcNow,
            ArchivedUtc = archivedUtc
        };
        db.EquipmentDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    public string WritePdf(
        Guid equipmentId,
        EquipmentDocumentType type,
        string fileName = "stored.pdf")
    {
        var relativePath = Path.Combine(
            equipmentId.ToString("N"),
            type.ToString(),
            fileName);
        var absolutePath = Path.Combine(StoragePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllBytes(
            absolutePath,
            "%PDF-1.4\n%%EOF"u8.ToArray());
        return relativePath.Replace('\\', '/');
    }

    public async Task<TResult> WithDbAsync<TResult>(
        Func<FsmDbContext, Task<TResult>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsmDbContext>();
        return await action(db);
    }

    private static User CreateUser(Guid id, string name, UserRole role) => new()
    {
        Id = id,
        FullName = name,
        Email = $"{name.ToLowerInvariant()}@test.local",
        PasswordHash = "unused",
        Role = role
    };

    private static Equipment CreateEquipment(
        Guid id,
        Guid vesselId,
        string name) => new()
    {
        Id = id,
        VesselId = vesselId,
        Category = EquipmentCategory.LSA,
        EquipmentName = name,
        InspectionBy = InspectionBy.Crew
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || !Directory.Exists(testRoot))
            return;

        SqliteConnection.ClearAllPools();
        Directory.Delete(testRoot, recursive: true);
    }
}
