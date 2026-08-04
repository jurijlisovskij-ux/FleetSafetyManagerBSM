using Microsoft.Data.Sqlite;

namespace FSM.Api;

public static class DatabaseConnectionResolver
{
    public static string Resolve(string connectionString, string applicationDirectory)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) ||
            builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var intendedPath = Path.GetFullPath(Path.IsPathRooted(builder.DataSource)
            ? builder.DataSource
            : Path.Combine(applicationDirectory, builder.DataSource));
        if (!File.Exists(intendedPath))
        {
            var existing = FindExistingDatabase(applicationDirectory, intendedPath);
            if (existing is not null)
            {
                Console.WriteLine($"Opening existing Fleet Safety Manager database: {existing}");
                intendedPath = existing;
            }
            else
            {
                Console.WriteLine($"No existing database found; a new database will be created at: {intendedPath}");
            }
        }
        else
        {
            Console.WriteLine($"Opening Fleet Safety Manager database: {intendedPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(intendedPath)!);
        builder.DataSource = intendedPath;
        return builder.ToString();
    }

    private static string? FindExistingDatabase(string applicationDirectory, string intendedPath)
    {
        var releaseDirectory = new DirectoryInfo(applicationDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (releaseDirectory.Parent?.Name.Equals("Releases", StringComparison.OrdinalIgnoreCase) != true)
            return null;

        var installRoot = releaseDirectory.Parent.Parent;
        if (installRoot is null) return null;

        var rootDatabase = Path.Combine(installRoot.FullName, "Data", "fsm.db");
        if (File.Exists(rootDatabase)) return rootDatabase;

        return releaseDirectory.Parent.EnumerateDirectories("RC*_*", SearchOption.TopDirectoryOnly)
            .Where(x => !x.FullName.Equals(releaseDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            .Select(x => new FileInfo(Path.Combine(x.FullName, "Data", "fsm.db")))
            .Where(x => x.Exists && !x.FullName.Equals(intendedPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .Select(x => x.FullName)
            .FirstOrDefault();
    }
}
