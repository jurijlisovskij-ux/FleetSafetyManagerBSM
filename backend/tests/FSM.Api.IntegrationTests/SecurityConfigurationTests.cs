using System.Net;
using System.Net.Http.Json;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FSM.Api.IntegrationTests;

public sealed class SecurityConfigurationTests
{
    private const string ValidKey =
        "security-test-signing-key-with-at-least-32-characters";

    [Fact]
    public void Missing_jwt_key_prevents_startup()
    {
        using var factory = CreateFactory(
            "Production",
            new Dictionary<string, string?>
            {
                ["Jwt:Key"] = ""
            });

        var exception = Assert.ThrowsAny<Exception>(
            () => factory.CreateClient());

        Assert.Contains(
            "JWT signing key is missing",
            FlattenMessages(exception));
    }

    [Fact]
    public void Placeholder_jwt_key_prevents_startup()
    {
        using var factory = CreateFactory(
            "Production",
            new Dictionary<string, string?>
            {
                ["Jwt:Key"] =
                    "CHANGE_THIS_KEY_BEFORE_REAL_USE_12345678901234567890"
            });

        var exception = Assert.ThrowsAny<Exception>(
            () => factory.CreateClient());

        Assert.Contains(
            "must not use a placeholder",
            FlattenMessages(exception));
    }

    [Fact]
    public void Short_jwt_key_prevents_startup()
    {
        using var factory = CreateFactory(
            "Production",
            new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "too-short"
            });

        var exception = Assert.ThrowsAny<Exception>(
            () => factory.CreateClient());

        Assert.Contains(
            "at least 32 characters",
            FlattenMessages(exception));
    }

    [Fact]
    public async Task Valid_jwt_key_starts_successfully()
    {
        using var factory = CreateFactory(
            "Production",
            ValidSettings());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_is_disabled_outside_development()
    {
        using var factory = CreateFactory(
            "Production",
            ValidSettings());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_is_enabled_in_development()
    {
        using var factory = CreateFactory(
            "Development",
            ValidSettings());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cors_allows_only_configured_origin()
    {
        using var factory = CreateFactory(
            "Production",
            ValidSettings());
        using var client = factory.CreateClient();

        using var allowedRequest =
            new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        allowedRequest.Headers.Add("Origin", "http://localhost:5173");
        var allowedResponse = await client.SendAsync(allowedRequest);

        using var deniedRequest =
            new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        deniedRequest.Headers.Add("Origin", "https://untrusted.example");
        var deniedResponse = await client.SendAsync(deniedRequest);

        Assert.Equal(
            "http://localhost:5173",
            allowedResponse.Headers
                .GetValues("Access-Control-Allow-Origin")
                .Single());
        Assert.False(
            deniedResponse.Headers.Contains(
                "Access-Control-Allow-Origin"));
    }

    [Fact]
    public void Development_seed_enabled_without_passwords_fails_safely()
    {
        var settings = ValidSettings();
        settings["DevelopmentSeed:Enabled"] = "true";

        using var factory = CreateFactory("Development", settings);
        var exception = Assert.ThrowsAny<Exception>(
            () => factory.CreateClient());

        Assert.Contains(
            "DevelopmentSeed:AdministratorPassword is missing",
            FlattenMessages(exception));
    }

    [Fact]
    public async Task Development_seed_is_disabled_by_default()
    {
        using var factory = CreateFactory(
            "Production",
            ValidSettings());
        using var client = factory.CreateClient();

        _ = await client.GetAsync("/api/auth/me");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsmDbContext>();
        Assert.False(await db.Users.AnyAsync());
    }

    [Fact]
    public async Task Development_seed_uses_configured_passwords()
    {
        var settings = ValidSettings();
        settings["DevelopmentSeed:Enabled"] = "true";
        settings["DevelopmentSeed:AdministratorPassword"] =
            "ConfiguredAdminPassword!";
        settings["DevelopmentSeed:SuperintendentPassword"] =
            "ConfiguredSuperintendentPassword!";
        settings["DevelopmentSeed:CrewPassword"] =
            "ConfiguredCrewPassword!";

        using var factory = CreateFactory("Development", settings);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new
            {
                email = "admin@fsm.local",
                password = "ConfiguredAdminPassword!"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Login_source_has_no_prefilled_credentials_and_clears_password()
    {
        var sourcePath = FindRepositoryFile(
            "frontend",
            "src",
            "main.tsx");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains(
            "const [email, setEmail] = useState(\"\")",
            source);
        Assert.Contains(
            "const [password, setPassword] = useState(\"\")",
            source);
        Assert.DoesNotContain("Super123!", source);
        Assert.DoesNotContain("Admin123!", source);
        Assert.True(
            source.Split("setPassword(\"\")").Length - 1 >= 2,
            "Password state must be cleared after authentication and logout.");
        Assert.DoesNotContain("localStorage.setItem", source);
        Assert.DoesNotContain(
            "sessionStorage.setItem(\"password",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    private static SecurityTestFactory CreateFactory(
        string environment,
        Dictionary<string, string?> settings) =>
        new(environment, settings);

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["Jwt:Key"] = ValidKey,
        ["DevelopmentSeed:Enabled"] = "false",
        ["Swagger:Enabled"] = "false",
        ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
        ["Notifications:Enabled"] = "false"
    };

    private static string FlattenMessages(Exception exception)
    {
        var messages = new List<string>();

        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(Environment.NewLine, messages);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var path = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());

            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Unable to locate repository file {Path.Combine(segments)}.");
    }

    private sealed class SecurityTestFactory(
        string environment,
        IReadOnlyDictionary<string, string?> settings)
        : WebApplicationFactory<Program>
    {
        private readonly string testRoot = Path.Combine(
            Path.GetTempPath(),
            "fsm-security-tests",
            Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(testRoot);
            builder.UseEnvironment(environment);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>(settings)
                {
                    ["ConnectionStrings:DefaultConnection"] =
                        $"Data Source={Path.Combine(testRoot, "security.db")}",
                    ["Storage:RootPath"] =
                        Path.Combine(testRoot, "documents")
                };
                configuration.AddInMemoryCollection(values);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing || !Directory.Exists(testRoot))
                return;

            SqliteConnection.ClearAllPools();
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
