using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FSM.Domain.Entities;
using FSM.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FSM.Api.IntegrationTests;

public sealed class InspectionReportTests : IClassFixture<ApiTestFactory>, IAsyncLifetime
{
    private readonly ApiTestFactory factory;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    public InspectionReportTests(ApiTestFactory factory) => this.factory = factory;
    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Endpoints_require_authentication()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/inspection-reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/inspection-reports/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Vessel_authorization_and_missing_report_are_enforced()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var superintendent = factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        var assigned = await Create(admin, ApiTestFactory.AssignedVesselId);
        var other = await Create(admin, ApiTestFactory.OtherVesselId);

        Assert.Equal(HttpStatusCode.OK,
            (await superintendent.GetAsync($"/api/inspection-reports/{assigned.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await superintendent.GetAsync($"/api/inspection-reports/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/inspection-reports/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Crew_can_only_read_assigned_finalized_reports()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var crew = factory.CreateAuthenticatedClient(ApiTestFactory.CrewId);
        var report = await Create(admin, ApiTestFactory.AssignedVesselId);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await crew.GetAsync($"/api/inspection-reports/{report.Id}")).StatusCode);
        (await admin.PostAsync($"/api/inspection-reports/{report.Id}/finalize", null))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK,
            (await crew.GetAsync($"/api/inspection-reports/{report.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await crew.PostAsJsonAsync($"/api/inspection-reports/{report.Id}/findings",
                FindingRequest())).StatusCode);
    }

    [Fact]
    public async Task Report_numbers_sequence_by_vessel_and_year()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var first = await Create(admin, ApiTestFactory.AssignedVesselId, "2026-01-01");
        var next = await Create(admin, ApiTestFactory.AssignedVesselId, "2026-12-01");
        var newYear = await Create(admin, ApiTestFactory.AssignedVesselId, "2027-01-01");
        var other = await Create(admin, ApiTestFactory.OtherVesselId, "2026-01-01");
        Assert.EndsWith("-2026-001", first.ReportNumber);
        Assert.EndsWith("-2026-002", next.ReportNumber);
        Assert.EndsWith("-2027-001", newYear.ReportNumber);
        Assert.EndsWith("-2026-001", other.ReportNumber);
    }

    [Fact]
    public async Task Duplicate_report_number_is_prevented_by_database()
    {
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.InspectionReports.AddRange(
                Report("IR-DUPLICATE-2026-001", now),
                Report("IR-DUPLICATE-2026-001", now));
            await db.SaveChangesAsync();
            return true;
        }));
    }

    [Fact]
    public async Task Draft_findings_can_be_created_updated_reordered_closed_and_deleted()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var report = await Create(client, ApiTestFactory.AssignedVesselId);
        var first = await AddFinding(client, report.Id, "First");
        var second = await AddFinding(client, report.Id, "Second");
        var update = new
        {
            department = "Deck", equipmentOrArea = "Main deck",
            exactLocation = "Port side", description = "First updated",
            priority = "Normal", status = "Closed", inspectorRemarks = "Remark"
        };
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/inspection-findings/{first}", update)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync($"/api/inspection-reports/{report.Id}/findings/reorder",
                new { findingIds = new[] { second, first } })).StatusCode);
        var detail = await Get(client, report.Id);
        Assert.Equal(second, detail.GetProperty("findings")[0].GetProperty("id").GetGuid());
        Assert.Equal("Closed", detail.GetProperty("findings")[1].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/inspection-findings/{second}")).StatusCode);
    }

    [Fact]
    public async Task Finalized_report_is_read_only_and_admin_reopen_requires_reason()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var superintendent = factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        var report = await Create(admin, ApiTestFactory.AssignedVesselId);
        (await admin.PostAsync($"/api/inspection-reports/{report.Id}/finalize", null))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync($"/api/inspection-reports/{report.Id}/findings",
                FindingRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/inspection-reports/{report.Id}/reopen",
                new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await superintendent.PostAsJsonAsync($"/api/inspection-reports/{report.Id}/reopen",
                new { reason = "Correction" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsJsonAsync($"/api/inspection-reports/{report.Id}/reopen",
                new { reason = "Correction" })).StatusCode);
    }

    [Fact]
    public async Task Photos_validate_content_and_deletion_removes_file()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var report = await Create(client, ApiTestFactory.AssignedVesselId);
        var finding = await AddFinding(client, report.Id, "Photo finding");
        var invalid = await Upload(client, finding, "bad.jpg", "image/jpeg", "not-image"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var valid = await Upload(client, finding, "photo.png", "image/png", png);
        valid.EnsureSuccessStatusCode();
        var jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EB//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EB//2Q==");
        (await Upload(client, finding, "photo.jpg", "image/jpeg", jpeg))
            .EnsureSuccessStatusCode();
        var photoId = (await valid.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        var path = await factory.WithDbAsync(async db =>
            (await db.InspectionPhotos.AsNoTracking().SingleAsync(x => x.Id == photoId))
            .StoredFileName);
        Assert.True(File.Exists(Path.Combine(factory.StoragePath,
            path.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/inspection-photos/{photoId}")).StatusCode);
        Assert.False(File.Exists(Path.Combine(factory.StoragePath,
            path.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData("..\\outside.jpg")]
    [InlineData("C:\\Windows\\photo.jpg")]
    [InlineData("/etc/photo.jpg")]
    public void Inspection_storage_rejects_rooted_and_traversal_paths(string path)
    {
        using var scope = factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<InspectionStorageService>();
        Assert.Throws<InvalidOperationException>(() => storage.GetAbsolutePath(path));
    }

    [Fact]
    public async Task Pdf_generation_versions_and_authorized_download_work()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var superintendent = factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        var report = await Create(admin, ApiTestFactory.AssignedVesselId);
        await AddFinding(admin, report.Id, "PDF finding");
        (await admin.PostAsync($"/api/inspection-reports/{report.Id}/finalize", null))
            .EnsureSuccessStatusCode();
        var first = await admin.PostAsync($"/api/inspection-reports/{report.Id}/generate-pdf", null);
        first.EnsureSuccessStatusCode();
        var firstDto = await first.Content.ReadFromJsonAsync<JsonElement>();
        var second = await admin.PostAsync($"/api/inspection-reports/{report.Id}/generate-pdf", null);
        second.EnsureSuccessStatusCode();
        var versions = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/inspection-reports/{report.Id}/pdf-versions");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Single(versions.EnumerateArray(),
            x => x.GetProperty("isCurrent").GetBoolean());
        var oldId = firstDto.GetProperty("id").GetGuid();
        var download = await superintendent.GetAsync(
            $"/api/inspection-report-pdfs/{oldId}/download");
        download.EnsureSuccessStatusCode();
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF-"u8.ToArray(), bytes[..5]);
    }

    [Fact]
    public async Task Cross_vessel_photo_and_pdf_downloads_are_forbidden()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var superintendent = factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        var report = await Create(admin, ApiTestFactory.OtherVesselId);
        (await admin.PostAsync($"/api/inspection-reports/{report.Id}/finalize", null))
            .EnsureSuccessStatusCode();
        var generated = await admin.PostAsync($"/api/inspection-reports/{report.Id}/generate-pdf", null);
        var pdf = await generated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await superintendent.GetAsync(
                $"/api/inspection-report-pdfs/{pdf.GetProperty("id").GetGuid()}/download")).StatusCode);
    }

    private async Task<(Guid Id, string ReportNumber)> Create(
        HttpClient client, Guid vessel, string date = "2026-06-01")
    {
        var response = await client.PostAsJsonAsync("/api/inspection-reports", new
        {
            vesselId = vessel, inspectionDate = date, inspectorName = "Inspector",
            inspectionType = "DeckAndEngine", generalRemarks = "General remarks"
        });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetGuid(), json.GetProperty("reportNumber").GetString()!);
    }
    private static async Task<Guid> AddFinding(HttpClient client, Guid report, string description)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/inspection-reports/{report}/findings", FindingRequest(description));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static object FindingRequest(string description = "Description") => new
    {
        department = "Deck", equipmentOrArea = "Main deck", exactLocation = "Port side",
        description, priority = "Normal", status = "Open", inspectorRemarks = "Remark"
    };
    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid finding,
        string name, string mime, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        content.Add(file, "file", name);
        return await client.PostAsync($"/api/inspection-findings/{finding}/photos", content);
    }
    private static async Task<JsonElement> Get(HttpClient client, Guid report) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/inspection-reports/{report}");
    private static InspectionReport Report(string number, DateTime now) => new()
    {
        Id = Guid.NewGuid(), VesselId = ApiTestFactory.AssignedVesselId,
        ReportNumber = number, InspectionDate = new DateOnly(2026, 1, 1),
        InspectorName = "Inspector", State = InspectionReportState.Draft,
        CreatedUtc = now, UpdatedUtc = now
    };
}
