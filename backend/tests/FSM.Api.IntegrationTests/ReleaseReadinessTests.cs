using System.Reflection;
using System.Text.Json;
using FSM.Api.Services;
using Xunit;

namespace FSM.Api.IntegrationTests;

public sealed class ReleaseReadinessTests
{
    [Fact]
    public void Backend_and_runtime_versions_are_consistent()
    {
        var informational = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.StartsWith(SystemVersions.ApplicationVersion, informational);
        Assert.Equal("1.0.0-rc1", SystemVersions.ApplicationVersion);
    }

    [Fact]
    public void Production_template_is_safe_and_has_explicit_paths()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "deployment", "appsettings.Production.template.json")));
        var config = document.RootElement;
        Assert.Equal("", config.GetProperty("Jwt").GetProperty("Key").GetString());
        Assert.False(config.GetProperty("DevelopmentSeed").GetProperty("Enabled").GetBoolean());
        Assert.False(config.GetProperty("Swagger").GetProperty("Enabled").GetBoolean());
        Assert.NotEmpty(config.GetProperty("Cors").GetProperty("AllowedOrigins").EnumerateArray());
        Assert.Contains("%ProgramData%", config.GetProperty("ConnectionStrings")
            .GetProperty("DefaultConnection").GetString());
        Assert.Contains("%ProgramData%", config.GetProperty("Storage")
            .GetProperty("RootPath").GetString());
        Assert.Contains("%ProgramData%", config.GetProperty("Administration")
            .GetProperty("BackupPath").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "deployment")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
