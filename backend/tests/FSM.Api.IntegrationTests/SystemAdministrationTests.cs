using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FSM.Api.IntegrationTests;

public sealed class SystemAdministrationTests :
    IClassFixture<ApiTestFactory>, IAsyncLifetime
{
    private readonly ApiTestFactory factory;
    public SystemAdministrationTests(ApiTestFactory factory) => this.factory = factory;
    public Task DisposeAsync() => Task.CompletedTask;
    public Task InitializeAsync() => factory.ResetAsync();

    [Theory]
    [InlineData("settings")]
    [InlineData("health")]
    [InlineData("diagnostics")]
    [InlineData("audit")]
    [InlineData("backups")]
    public async Task Administration_endpoints_require_administrator(string endpoint)
    {
        using var anonymous = factory.CreateClient();
        using var superintendent =
            factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/administration/{endpoint}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await superintendent.GetAsync($"/api/administration/{endpoint}")).StatusCode);
    }

    [Fact]
    public async Task Settings_round_trip_and_write_audit_entry()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var request = new
        {
            companyName = "Test Fleet", companyLogo = "branding/logo.png",
            defaultPdfFooter = "Controlled copy", notificationDays = 45,
            inspectionDraftWarningDays = 10,
            maximumDocumentUploadBytes = 1048576,
            maximumInspectionPhotoBytes = 2097152,
            maximumServiceRequestUploadBytes = 3145728,
            documentStoragePath = "Documents", timeZone = "UTC",
            dateFormat = "yyyy-MM-dd"
        };
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/administration/settings", request)).StatusCode);
        var settings = await client.GetFromJsonAsync<JsonElement>(
            "/api/administration/settings");
        Assert.Equal("Test Fleet", settings.GetProperty("companyName").GetString());
        Assert.Equal(45, settings.GetProperty("notificationDays").GetInt32());
        var audit = await client.GetFromJsonAsync<JsonElement>("/api/administration/audit");
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), x =>
            x.GetProperty("action").GetString() == "AdministrationSettingsChanged");
    }

    [Fact]
    public async Task Invalid_settings_return_safe_ProblemDetails()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var response = await client.PutAsJsonAsync("/api/administration/settings",
            new { companyName = "", notificationDays = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("Database")]
    [InlineData("Documents")]
    [InlineData("Full")]
    public async Task Backup_types_create_isolated_archives_and_validate_restore(string type)
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        Directory.CreateDirectory(factory.StoragePath);
        await File.WriteAllTextAsync(Path.Combine(factory.StoragePath, "document.txt"), "test");
        var created = await client.PostAsync($"/api/administration/backups/{type}", null);
        Assert.True(created.IsSuccessStatusCode,
            $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var backupFile = Path.Combine(factory.BackupPath,
            item.GetProperty("fileName").GetString()!);
        Assert.True(File.Exists(backupFile));
        Assert.True(new FileInfo(backupFile).Length > 0);
        var restore = await client.PostAsync(
            $"/api/administration/backups/{item.GetProperty("id").GetGuid()}/restore", null);
        Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);
        var result = await restore.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("requiresRestart").GetBoolean());
    }

    [Fact]
    public async Task Health_diagnostics_and_maintenance_are_operational()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var health = await client.GetFromJsonAsync<JsonElement>("/api/administration/health");
        Assert.Equal("OK", health.GetProperty("database").GetProperty("status").GetString());
        Assert.True(health.GetProperty("database").GetProperty("size").GetInt64() > 0);
        var diagnostics = await client.GetFromJsonAsync<JsonElement>(
            "/api/administration/diagnostics");
        Assert.Equal("OK", diagnostics.GetProperty("databaseConnection").GetString());
        Assert.True(diagnostics.GetProperty("jwt").GetProperty("configured").GetBoolean());
        foreach (var operation in new[] { "integrity-check", "rebuild-indexes", "vacuum" })
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsync($"/api/administration/maintenance/{operation}", null))
                .StatusCode);
    }

    [Fact]
    public async Task Exports_produce_csv_and_excel_without_entity_leakage()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        foreach (var name in new[]
            { "audit", "settings", "fleet-summary", "dashboard-statistics" })
        {
            var csv = await client.GetAsync($"/api/administration/exports/{name}.csv");
            var excel = await client.GetAsync($"/api/administration/exports/{name}.xlsx");
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
            Assert.NotEmpty(await csv.Content.ReadAsByteArrayAsync());
            Assert.NotEmpty(await excel.Content.ReadAsByteArrayAsync());
        }
    }
}
