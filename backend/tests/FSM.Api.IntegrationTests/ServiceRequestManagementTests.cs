using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FSM.Api.Services;
using FSM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FSM.Api.IntegrationTests;

public sealed class ServiceRequestManagementTests :
    IClassFixture<ApiTestFactory>, IAsyncLifetime
{
    private readonly ApiTestFactory factory;
    public ServiceRequestManagementTests(ApiTestFactory factory) => this.factory = factory;
    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Endpoints_require_authentication()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/service-requests")).StatusCode);
    }

    [Fact]
    public async Task Vessel_authorization_and_missing_request_are_enforced()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var superintendent = factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        var assigned = await Create(admin, ApiTestFactory.AssignedVesselId);
        var other = await Create(admin, ApiTestFactory.OtherVesselId);
        Assert.Equal(HttpStatusCode.OK,
            (await superintendent.GetAsync($"/api/service-requests/{assigned.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await superintendent.GetAsync($"/api/service-requests/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/service-requests/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Numbering_is_independent_by_vessel_and_year()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var first = await Create(admin, ApiTestFactory.AssignedVesselId, "2026-01-01");
        var next = await Create(admin, ApiTestFactory.AssignedVesselId, "2026-12-01");
        var reset = await Create(admin, ApiTestFactory.AssignedVesselId, "2027-01-01");
        var other = await Create(admin, ApiTestFactory.OtherVesselId, "2026-01-01");
        Assert.EndsWith("-2026-001", first.Number);
        Assert.EndsWith("-2026-002", next.Number);
        Assert.EndsWith("-2027-001", reset.Number);
        Assert.EndsWith("-2026-001", other.Number);
    }

    [Fact]
    public async Task Database_prevents_duplicate_request_number()
    {
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.ServiceRequests.AddRange(Entity("SR-DUP-2026-001", now),
                Entity("SR-DUP-2026-001", now));
            await db.SaveChangesAsync(); return true;
        }));
    }

    [Fact]
    public async Task Equipment_manual_and_finding_items_use_snapshots_and_reject_cross_vessel()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var request = await Create(admin, ApiTestFactory.AssignedVesselId);
        var findingId = await AddFinalizedFinding();
        (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/items", new {
            itemType = "Equipment", sourceIds = new[] { ApiTestFactory.AssignedEquipmentId },
            serviceDescription = "Annual service" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/items", new {
            itemType = "InspectionFinding", sourceIds = new[] { findingId },
            serviceDescription = "Rectify finding" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/items", new {
            itemType = "Manual", title = "Manual task", serviceDescription = "Inspect area"
        })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/items", new {
                itemType = "Equipment", sourceIds = new[] { ApiTestFactory.OtherEquipmentId },
                serviceDescription = "Invalid"
            })).StatusCode);
        var detail = await Get(admin, request.Id);
        Assert.Equal(3, detail.GetProperty("items").GetArrayLength());
        Assert.Equal("EPIRB", detail.GetProperty("items")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Duplicate_items_are_rejected_and_draft_refresh_is_explicit()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var request = await Create(admin, ApiTestFactory.AssignedVesselId);
        var input = new { itemType = "Equipment",
            sourceIds = new[] { ApiTestFactory.AssignedEquipmentId },
            serviceDescription = "Service" };
        (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/items", input))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/items", input)).StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var source = await db.Equipment.SingleAsync(x => x.Id == ApiTestFactory.AssignedEquipmentId);
            source.EquipmentName = "Changed EPIRB"; await db.SaveChangesAsync(); return true;
        });
        Assert.Equal("EPIRB", (await Get(admin, request.Id))
            .GetProperty("items")[0].GetProperty("title").GetString());
        (await admin.PostAsync($"/api/service-requests/{request.Id}/items/refresh-equipment", null))
            .EnsureSuccessStatusCode();
        Assert.Equal("Changed EPIRB", (await Get(admin, request.Id))
            .GetProperty("items")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Workflow_enforces_transitions_reasons_and_read_only_state()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var superintendent = factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        var request = await Create(admin, ApiTestFactory.AssignedVesselId);
        Assert.Equal(HttpStatusCode.Conflict,
            (await superintendent.PostAsync($"/api/service-requests/{request.Id}/mark-sent", null)).StatusCode);
        (await superintendent.PostAsync($"/api/service-requests/{request.Id}/mark-ready", null))
            .EnsureSuccessStatusCode();
        (await superintendent.PostAsync($"/api/service-requests/{request.Id}/mark-sent", null))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/items", new {
                itemType = "Manual", title = "No", serviceDescription = "No"
            })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await superintendent.PostAsJsonAsync($"/api/service-requests/{request.Id}/reopen",
                new { reason = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/reopen",
                new { reason = "" })).StatusCode);
        (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/reopen",
            new { reason = "Correction" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Cancellation_requires_reason_and_crew_cannot_change_workflow()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var crew = factory.CreateAuthenticatedClient(ApiTestFactory.CrewId);
        var request = await Create(admin, ApiTestFactory.AssignedVesselId);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/cancel",
                new { reason = "" })).StatusCode);
        (await admin.PostAsJsonAsync($"/api/service-requests/{request.Id}/cancel",
            new { reason = "No longer required" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await crew.PostAsJsonAsync($"/api/service-requests/{request.Id}/reopen",
                new { reason = "No" })).StatusCode);
    }

    [Fact]
    public async Task Attachments_validate_supported_content_cleanup_and_download_authorization()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var superintendent = factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        var request = await Create(admin, ApiTestFactory.OtherVesselId);
        var pdf = await Upload(admin, request.Id, "document.pdf",
            "application/pdf", "%PDF-1.4\n%%EOF"u8.ToArray());
        pdf.EnsureSuccessStatusCode();
        var id = (await pdf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await superintendent.GetAsync($"/api/service-request-attachments/{id}/download")).StatusCode);
        var path = await factory.WithDbAsync(async db =>
            (await db.ServiceRequestAttachments.AsNoTracking().SingleAsync(x => x.Id == id))
            .StoredFileName);
        Assert.True(File.Exists(Path.Combine(factory.StoragePath,
            path.Replace('/', Path.DirectorySeparatorChar))));
        (await admin.DeleteAsync($"/api/service-request-attachments/{id}"))
            .EnsureSuccessStatusCode();
        Assert.False(File.Exists(Path.Combine(factory.StoragePath,
            path.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Theory]
    [InlineData(".docx", "word/document.xml")]
    [InlineData(".xlsx", "xl/workbook.xml")]
    public async Task Office_attachments_require_valid_zip_structure(
        string extension, string entry)
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var request = await Create(admin, ApiTestFactory.AssignedVesselId);
        var valid = OfficeFile(entry);
        (await Upload(admin, request.Id, $"valid{extension}",
            "application/octet-stream", valid)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Upload(admin, request.Id, $"bad{extension}",
                "application/octet-stream", "bad"u8.ToArray())).StatusCode);
    }

    [Fact]
    public async Task Image_attachments_validate_actual_content_and_size_limit()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var request = await Create(admin, ApiTestFactory.AssignedVesselId);
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EB//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EB//2Q==");
        (await Upload(admin, request.Id, "photo.png", "image/png", png))
            .EnsureSuccessStatusCode();
        (await Upload(admin, request.Id, "photo.jpg", "image/jpeg", jpeg))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Upload(admin, request.Id, "bad.png", "image/png",
                "not-an-image"u8.ToArray())).StatusCode);
        var oversized = new byte[25 * 1024 * 1024 + 2048];
        var oversizedResponse = await Upload(admin, request.Id, "large.pdf",
            "application/pdf", oversized);
        Assert.Contains(oversizedResponse.StatusCode,
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge });
    }

    [Theory]
    [InlineData("../escape.pdf")]
    [InlineData("..\\escape.pdf")]
    [InlineData("C:\\Windows\\escape.pdf")]
    public void Storage_rejects_unsafe_paths(string path)
    {
        using var scope = factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<ServiceRequestStorageService>();
        Assert.Throws<InvalidOperationException>(() => storage.GetAbsolutePath(path));
    }

    [Fact]
    public async Task Pdf_versions_retain_archives_and_authorize_downloads()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var superintendent = factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        var request = await Create(admin, ApiTestFactory.AssignedVesselId);
        var first = await admin.PostAsync($"/api/service-requests/{request.Id}/generate-pdf", null);
        first.EnsureSuccessStatusCode();
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/service-requests/{request.Id}/generate-pdf", null))
            .EnsureSuccessStatusCode();
        var versions = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/service-requests/{request.Id}/pdf-versions");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Single(versions.EnumerateArray(), x => x.GetProperty("isCurrent").GetBoolean());
        var download = await superintendent.GetAsync(
            $"/api/service-request-pdfs/{firstId}/download");
        download.EnsureSuccessStatusCode();
        Assert.Equal("%PDF-"u8.ToArray(), (await download.Content.ReadAsByteArrayAsync())[..5]);
    }

    private async Task<(Guid Id, string Number)> Create(HttpClient client, Guid vessel,
        string date = "2026-06-01")
    {
        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            vesselId = vessel, requestDate = date, contactName = "Contact",
            contactEmail = "contact@test.local", preferredPort = (string?)null,
            subject = "Annual equipment service"
        });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetGuid(),
            json.GetProperty("requestNumber").GetString()!);
    }
    private async Task<Guid> AddFinalizedFinding() => await factory.WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        var report = new InspectionReport
        {
            Id = Guid.NewGuid(), VesselId = ApiTestFactory.AssignedVesselId,
            ReportNumber = $"IR-TEST-{Guid.NewGuid():N}", InspectionDate = new DateOnly(2026, 1, 1),
            InspectorName = "Inspector", State = InspectionReportState.Finalized,
            CreatedUtc = now, UpdatedUtc = now
        };
        var finding = new InspectionFinding
        {
            Id = Guid.NewGuid(), InspectionReport = report, SequenceNumber = 1,
            EquipmentOrArea = "Deck crane", Description = "Finding",
            Priority = InspectionPriority.Urgent, CreatedUtc = now, UpdatedUtc = now
        };
        db.Add(finding); await db.SaveChangesAsync(); return finding.Id;
    });
    private static async Task<JsonElement> Get(HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/service-requests/{id}");
    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid id,
        string name, string mime, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(content, "file", name);
        return await client.PostAsync($"/api/service-requests/{id}/attachments", form);
    }
    private static byte[] OfficeFile(string entry)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("[Content_Types].xml");
            zip.CreateEntry(entry);
        }
        return stream.ToArray();
    }
    private static ServiceRequest Entity(string number, DateTime now) => new()
    {
        Id = Guid.NewGuid(), RequestNumber = number,
        VesselId = ApiTestFactory.AssignedVesselId,
        CreatedByUserId = ApiTestFactory.AdminId,
        RequestDate = new DateOnly(2026, 1, 1), RequestedBy = "Admin",
        ContactName = "Contact", ContactEmail = "contact@test.local",
        Subject = "Subject", Status = ServiceRequestStatus.Draft,
        CreatedUtc = now, UpdatedUtc = now
    };
}
