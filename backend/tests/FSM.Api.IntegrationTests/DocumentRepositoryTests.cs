using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FSM.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FSM.Api.IntegrationTests;

public sealed class DocumentRepositoryTests :
    IClassFixture<ApiTestFactory>,
    IAsyncLifetime
{
    private readonly ApiTestFactory factory;

    public DocumentRepositoryTests(ApiTestFactory factory)
    {
        this.factory = factory;
    }

    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Document_endpoints_require_authentication()
    {
        using var client = factory.CreateClient();

        var list = await client.GetAsync(
            $"/api/equipment/{ApiTestFactory.AssignedEquipmentId}/documents");
        var metadata = await client.GetAsync(
            $"/api/documents/{Guid.NewGuid()}");
        var download = await client.GetAsync(
            $"/api/documents/{Guid.NewGuid()}/download");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, download.StatusCode);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("superintendent", true)]
    [InlineData("crew", true)]
    public async Task Existing_roles_can_access_assigned_equipment(
        string role,
        bool expected)
    {
        var userId = role switch
        {
            "admin" => ApiTestFactory.AdminId,
            "superintendent" => ApiTestFactory.SuperintendentId,
            _ => ApiTestFactory.CrewId
        };
        using var client = factory.CreateAuthenticatedClient(userId);

        var response = await client.GetAsync(
            $"/api/equipment/{ApiTestFactory.AssignedEquipmentId}/documents");

        Assert.Equal(
            expected ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            response.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_access_another_vessel()
    {
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        var response = await client.GetAsync(
            $"/api/equipment/{ApiTestFactory.OtherEquipmentId}/documents");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("superintendent")]
    [InlineData("crew")]
    public async Task Assigned_non_admin_cannot_access_unassigned_vessel(
        string role)
    {
        var userId = role == "superintendent"
            ? ApiTestFactory.SuperintendentId
            : ApiTestFactory.CrewId;
        using var client = factory.CreateAuthenticatedClient(userId);

        var response = await client.GetAsync(
            $"/api/equipment/{ApiTestFactory.OtherEquipmentId}/documents");

        await AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "Access denied");
    }

    [Fact]
    public async Task Missing_equipment_returns_safe_problem_details()
    {
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        var response = await client.GetAsync(
            $"/api/equipment/{Guid.NewGuid()}/documents");

        await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "Equipment not found");
    }

    [Fact]
    public async Task Document_on_another_vessel_is_forbidden()
    {
        var path = factory.WritePdf(
            ApiTestFactory.OtherEquipmentId,
            EquipmentDocumentType.Certificate);
        var document = await factory.AddDocumentAsync(
            ApiTestFactory.OtherEquipmentId,
            EquipmentDocumentType.Certificate,
            true,
            path);
        using var client = factory.CreateAuthenticatedClient(
            ApiTestFactory.SuperintendentId);

        var response = await client.GetAsync(
            $"/api/documents/{document.Id}");

        await AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "Access denied");
    }

    [Fact]
    public async Task List_metadata_download_and_archive_are_available()
    {
        var currentPath = factory.WritePdf(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            "current.pdf");
        var archivePath = factory.WritePdf(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            "archive.pdf");
        var current = await factory.AddDocumentAsync(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            true,
            currentPath,
            "current.pdf");
        var archived = await factory.AddDocumentAsync(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            false,
            archivePath,
            "archive.pdf",
            DateTime.UtcNow);
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/equipment/{ApiTestFactory.AssignedEquipmentId}/documents");
        Assert.Equal(2, list.GetArrayLength());
        Assert.Contains(
            list.EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == archived.Id &&
                    !item.GetProperty("isCurrent").GetBoolean());

        var metadata = await client.GetFromJsonAsync<JsonElement>(
            $"/api/documents/{current.Id}");
        Assert.Equal("Certificate", metadata.GetProperty("type").GetString());
        Assert.Equal("DOC-1", metadata.GetProperty("documentNumber").GetString());
        Assert.False(metadata.TryGetProperty("storedFileName", out _));

        var download = await client.GetAsync(
            $"/api/documents/{archived.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.StartsWith(
            "application/pdf",
            download.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Replacing_documents_archives_previous_current_documents()
    {
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        Assert.Equal(
            HttpStatusCode.OK,
            (await UploadPdfAsync(client, "certificate", "first.pdf")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await UploadPdfAsync(client, "certificate", "second.pdf")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await UploadPdfAsync(client, "report", "first-report.pdf")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await UploadPdfAsync(client, "report", "second-report.pdf")).StatusCode);

        var state = await factory.WithDbAsync(async db =>
            await db.EquipmentDocuments
                .AsNoTracking()
                .Where(x => x.EquipmentId == ApiTestFactory.AssignedEquipmentId)
                .OrderBy(x => x.DocumentType)
                .ThenBy(x => x.CreatedUtc)
                .ToListAsync());

        Assert.Equal(
            1,
            state.Count(x =>
                x.DocumentType == EquipmentDocumentType.Certificate &&
                x.IsCurrent));
        Assert.Equal(
            1,
            state.Count(x =>
                x.DocumentType == EquipmentDocumentType.ServiceReport &&
                x.IsCurrent));
        Assert.All(
            state.Where(x => !x.IsCurrent),
            document => Assert.NotNull(document.ArchivedUtc));

        var certificate = await client.GetAsync(
            $"/api/equipment/{ApiTestFactory.AssignedEquipmentId}/documents/certificate");
        var report = await client.GetAsync(
            $"/api/equipment/{ApiTestFactory.AssignedEquipmentId}/documents/report");
        Assert.Equal(HttpStatusCode.OK, certificate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
    }

    [Fact]
    public async Task Missing_file_returns_404_without_deleting_metadata()
    {
        var document = await factory.AddDocumentAsync(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            true,
            "missing/file.pdf");
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        var response = await client.GetAsync(
            $"/api/documents/{document.Id}/download");

        await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "Document not found");
        Assert.True(await factory.WithDbAsync(
            db => db.EquipmentDocuments.AnyAsync(x => x.Id == document.Id)));
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("..\\outside.pdf")]
    [InlineData(" ")]
    [InlineData("C:\\Windows\\system.ini")]
    public async Task Unsafe_stored_paths_are_rejected_without_path_disclosure(
        string storedPath)
    {
        var document = await factory.AddDocumentAsync(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            true,
            storedPath);
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        var response = await client.GetAsync(
            $"/api/documents/{document.Id}/download");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(factory.StoragePath, body);
        if (!string.IsNullOrWhiteSpace(storedPath))
            Assert.DoesNotContain(storedPath, body);
    }

    [Fact]
    public async Task Missing_document_returns_404_without_filesystem_details()
    {
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        var response = await client.GetAsync(
            $"/api/documents/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();

        await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "Document not found");
        Assert.DoesNotContain(factory.StoragePath, body);
    }

    [Fact]
    public async Task Invalid_upload_returns_problem_details_and_leaves_no_files()
    {
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        using var content = new MultipartFormDataContent();
        content.Add(
            new ByteArrayContent("not a pdf"u8.ToArray()),
            "file",
            "bad.pdf");
        var response = await client.PostAsync(
            $"/api/equipment/{ApiTestFactory.AssignedEquipmentId}/documents/certificate",
            content);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Invalid document");
        Assert.Empty(Directory.EnumerateFiles(
            factory.StoragePath,
            "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Intended_current_uniqueness_conflict_returns_409_and_rolls_back()
    {
        var existingPath = factory.WritePdf(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            "existing.pdf");
        var existing = await factory.AddDocumentAsync(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            true,
            existingPath,
            "existing.pdf");
        await factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER force_current_conflict
                BEFORE INSERT ON equipment_documents
                WHEN NEW.FileName = 'conflict.pdf'
                BEGIN
                    INSERT INTO equipment_documents (
                        Id, EquipmentId, DocumentType, FileName,
                        StoredFileName, MimeType, FileSize, IssueDate,
                        ExpiryDate, DocumentNumber, Issuer, Remarks,
                        IsCurrent, CreatedUtc, ArchivedUtc)
                    VALUES (
                        lower(hex(randomblob(16))), NEW.EquipmentId,
                        NEW.DocumentType, 'trigger.pdf', 'trigger.pdf',
                        'application/pdf', 0, NULL, NULL, NULL, NULL, NULL,
                        1, CURRENT_TIMESTAMP, NULL);
                END;
                """);
            return true;
        });
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        var response = await UploadPdfAsync(
            client,
            "certificate",
            "conflict.pdf");

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "Document conflict");
        var documents = await factory.WithDbAsync(async db =>
            await db.EquipmentDocuments.AsNoTracking().ToListAsync());
        Assert.Single(documents);
        Assert.Equal(existing.Id, documents[0].Id);
        Assert.True(documents[0].IsCurrent);
        Assert.Null(documents[0].ArchivedUtc);
        Assert.Single(
            Directory.EnumerateFiles(
                factory.StoragePath,
                "*",
                SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Database_failure_removes_new_file_and_rolls_back_replacement()
    {
        var existingPath = factory.WritePdf(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            "existing.pdf");
        var existing = await factory.AddDocumentAsync(
            ApiTestFactory.AssignedEquipmentId,
            EquipmentDocumentType.Certificate,
            true,
            existingPath,
            "existing.pdf");
        await factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER force_database_failure
                BEFORE INSERT ON equipment_documents
                WHEN NEW.FileName = 'database-failure.pdf'
                BEGIN
                    SELECT RAISE(ABORT, 'forced test database failure');
                END;
                """);
            return true;
        });
        using var client =
            factory.CreateAuthenticatedClient(ApiTestFactory.AdminId);

        var response = await UploadPdfAsync(
            client,
            "certificate",
            "database-failure.pdf");
        var body = await response.Content.ReadAsStringAsync();

        await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "Unexpected server error");
        Assert.DoesNotContain("forced test database failure", body);
        Assert.DoesNotContain(factory.DatabasePath, body);
        Assert.DoesNotContain(factory.StoragePath, body);

        var documents = await factory.WithDbAsync(async db =>
            await db.EquipmentDocuments.AsNoTracking().ToListAsync());
        Assert.Single(documents);
        Assert.Equal(existing.Id, documents[0].Id);
        Assert.True(documents[0].IsCurrent);
        Assert.Null(documents[0].ArchivedUtc);
        Assert.Single(
            Directory.EnumerateFiles(
                factory.StoragePath,
                "*",
                SearchOption.AllDirectories));
    }

    private async Task<HttpResponseMessage> UploadPdfAsync(
        HttpClient client,
        string type,
        string fileName)
    {
        using var content = new MultipartFormDataContent();
        content.Add(
            new ByteArrayContent("%PDF-1.4\n%%EOF"u8.ToArray()),
            "file",
            fileName);
        return await client.PostAsync(
            $"/api/equipment/{ApiTestFactory.AssignedEquipmentId}/documents/{type}",
            content);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(title, problem.GetProperty("title").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
    }
}
