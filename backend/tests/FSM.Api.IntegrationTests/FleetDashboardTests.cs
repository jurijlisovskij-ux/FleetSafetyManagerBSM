using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FSM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FSM.Api.IntegrationTests;

public sealed class FleetDashboardTests :
    IClassFixture<ApiTestFactory>, IAsyncLifetime
{
    private readonly ApiTestFactory factory;
    public FleetDashboardTests(ApiTestFactory factory) => this.factory = factory;
    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        await SeedOperationalData();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Dashboard_requires_authentication()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/fleet-dashboard")).StatusCode);
    }

    [Fact]
    public async Task Administrator_sees_all_vessels_and_aggregated_counts()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var json = await client.GetFromJsonAsync<JsonElement>("/api/fleet-dashboard");
        Assert.Equal(2, json.GetProperty("vessels").GetArrayLength());
        var counts = json.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("equipmentTotal").GetInt32());
        Assert.Equal(1, counts.GetProperty("equipmentOverdue").GetInt32());
        Assert.Equal(1, counts.GetProperty("inspectionDraft").GetInt32());
        Assert.Equal(1, counts.GetProperty("inspectionFinalized").GetInt32());
        Assert.Equal(1, counts.GetProperty("criticalOpen").GetInt32());
        Assert.Equal(1, counts.GetProperty("serviceReady").GetInt32());
        Assert.Equal(1, counts.GetProperty("serviceSent").GetInt32());
    }

    [Theory]
    [InlineData("superintendent")]
    [InlineData("crew")]
    public async Task Assigned_roles_see_only_their_vessel(string role)
    {
        var userId = role == "crew" ? ApiTestFactory.CrewId : ApiTestFactory.SuperintendentId;
        using var client = factory.CreateAuthenticatedClient(userId);
        var json = await client.GetFromJsonAsync<JsonElement>("/api/fleet-dashboard");
        var vessel = Assert.Single(json.GetProperty("vessels").EnumerateArray());
        Assert.Equal(ApiTestFactory.AssignedVesselId,
            vessel.GetProperty("vesselId").GetGuid());
    }

    [Fact]
    public async Task Cross_vessel_detail_is_forbidden()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.SuperintendentId);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync(
                $"/api/fleet-dashboard/vessels/{ApiTestFactory.OtherVesselId}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_filter_and_missing_vessel_return_ProblemDetails()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var invalid = await client.GetAsync("/api/fleet-dashboard?status=Blue");
        var missing = await client.GetAsync($"/api/fleet-dashboard/vessels/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json",
            invalid.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json",
            missing.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Filters_apply_to_rows_counts_and_search()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var vessel = await client.GetFromJsonAsync<JsonElement>(
            $"/api/fleet-dashboard?vesselId={ApiTestFactory.AssignedVesselId}");
        Assert.Single(vessel.GetProperty("vessels").EnumerateArray());
        Assert.Equal(1, vessel.GetProperty("counts").GetProperty("equipmentTotal").GetInt32());

        var status = await client.GetFromJsonAsync<JsonElement>(
            "/api/fleet-dashboard?status=Red");
        Assert.Single(status.GetProperty("vessels").EnumerateArray());

        var search = await client.GetFromJsonAsync<JsonElement>(
            "/api/fleet-dashboard?search=9000002");
        var other = Assert.Single(search.GetProperty("vessels").EnumerateArray());
        Assert.Equal(ApiTestFactory.OtherVesselId, other.GetProperty("vesselId").GetGuid());

        var category = await client.GetFromJsonAsync<JsonElement>(
            "/api/fleet-dashboard?category=LSA&year=2026");
        Assert.Equal(2, category.GetProperty("counts").GetProperty("equipmentTotal").GetInt32());
        var medicine = await client.GetFromJsonAsync<JsonElement>(
            "/api/fleet-dashboard?category=Medicine");
        Assert.DoesNotContain(medicine.GetProperty("notifications").EnumerateArray(), x =>
            x.GetProperty("title").GetString() == "Equipment expires within 30 days");
    }

    [Fact]
    public async Task Superintendent_filter_is_authorized_and_scoped()
    {
        using var admin = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        using var crew = factory.CreateAuthenticatedClient(ApiTestFactory.CrewId);
        var json = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/fleet-dashboard?superintendentId={ApiTestFactory.SuperintendentId}");
        Assert.Single(json.GetProperty("vessels").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await crew.GetAsync(
                $"/api/fleet-dashboard?superintendentId={ApiTestFactory.SuperintendentId}"))
            .StatusCode);
    }

    [Fact]
    public async Task Charts_and_notifications_reflect_operational_data_newest_first()
    {
        using var client = factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);
        var json = await client.GetFromJsonAsync<JsonElement>("/api/fleet-dashboard");
        Assert.Equal(4, json.GetProperty("charts").GetArrayLength());
        var notifications = json.GetProperty("notifications").EnumerateArray().ToList();
        Assert.Contains(notifications, x =>
            x.GetProperty("title").GetString() == "Equipment overdue");
        Assert.Contains(notifications, x =>
            x.GetProperty("title").GetString() == "Critical finding remains open");
        Assert.Contains(notifications, x =>
            x.GetProperty("title").GetString() == "Inspection draft requires attention");
        Assert.Contains(notifications, x =>
            x.GetProperty("title").GetString() == "Ready request not sent");
        Assert.True(notifications.Zip(notifications.Skip(1),
            (a, b) => a.GetProperty("eventUtc").GetDateTime() >=
                b.GetProperty("eventUtc").GetDateTime()).All(x => x));
    }

    private Task SeedOperationalData() => factory.WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        var assignedEquipment = await db.Equipment.SingleAsync(
            x => x.Id == ApiTestFactory.AssignedEquipmentId);
        assignedEquipment.ExpiryDate = DateOnly.FromDateTime(now.AddDays(-2));

        var draft = new InspectionReport
        {
            Id = Guid.NewGuid(), VesselId = ApiTestFactory.AssignedVesselId,
            ReportNumber = "IR-DASH-2026-001", InspectionDate = new DateOnly(2026, 1, 1),
            InspectorName = "Inspector", State = InspectionReportState.Draft,
            CreatedUtc = now.AddDays(-30), UpdatedUtc = now.AddDays(-30)
        };
        var finalized = new InspectionReport
        {
            Id = Guid.NewGuid(), VesselId = ApiTestFactory.OtherVesselId,
            ReportNumber = "IR-DASH-2026-002", InspectionDate = new DateOnly(2026, 1, 2),
            InspectorName = "Inspector", State = InspectionReportState.Finalized,
            CreatedUtc = now.AddDays(-5), UpdatedUtc = now.AddDays(-5)
        };
        var finding = new InspectionFinding
        {
            Id = Guid.NewGuid(), InspectionReport = draft, SequenceNumber = 1,
            EquipmentOrArea = "Main deck", Description = "Critical defect",
            Priority = InspectionPriority.Critical, Status = InspectionFindingStatus.Open,
            CreatedUtc = now.AddDays(-4), UpdatedUtc = now.AddDays(-1)
        };
        db.AddRange(draft, finalized, finding,
            Request("SR-DASH-2026-001", ApiTestFactory.AssignedVesselId,
                ServiceRequestStatus.Ready, now.AddHours(-2)),
            Request("SR-DASH-2026-002", ApiTestFactory.OtherVesselId,
                ServiceRequestStatus.Sent, now.AddHours(-1)));
        await db.SaveChangesAsync();
        return true;
    });

    private static ServiceRequest Request(string number, Guid vesselId,
        ServiceRequestStatus status, DateTime updated) => new()
    {
        Id = Guid.NewGuid(), RequestNumber = number, VesselId = vesselId,
        CreatedByUserId = ApiTestFactory.AdminId,
        RequestDate = new DateOnly(2026, 1, 1), RequestedBy = "Admin",
        ContactName = "Contact", ContactEmail = "contact@test.local",
        Subject = "Service", Status = status,
        CreatedUtc = updated.AddDays(-1), UpdatedUtc = updated
    };
}
