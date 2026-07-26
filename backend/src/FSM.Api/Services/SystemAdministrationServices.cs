using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace FSM.Api.Services;

public sealed class ActiveRequestTracker
{
    private int activeAuthenticatedRequests;
    public int Count => Volatile.Read(ref activeAuthenticatedRequests);
    public IDisposable Enter()
    {
        Interlocked.Increment(ref activeAuthenticatedRequests);
        return new Exit(this);
    }
    private sealed class Exit(ActiveRequestTracker owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                Interlocked.Decrement(ref owner.activeAuthenticatedRequests);
        }
    }
}

public sealed class ActiveRequestTrackingMiddleware(
    RequestDelegate next, ActiveRequestTracker tracker)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var lease = context.User.Identity?.IsAuthenticated == true
            ? tracker.Enter() : null;
        await next(context);
    }
}

public sealed class SystemAuditService(FsmDbContext db)
{
    public async Task WriteAsync(ClaimsPrincipal user, string action,
        string objectType, string? objectId, string? details, CancellationToken ct)
    {
        Guid? userId = Guid.TryParse(
            user.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed)
            ? parsed : null;
        db.SystemAuditEntries.Add(new SystemAuditEntry
        {
            CreatedUtc = DateTime.UtcNow,
            UserId = userId,
            UserName = user.Identity?.Name ?? "System",
            Action = action,
            ObjectType = objectType,
            ObjectId = objectId,
            Details = details
        });
        await db.SaveChangesAsync(ct);
    }
}

public sealed record BackupArtifact(string FileName, long Size);
public sealed record BackupManifest(
    string ApplicationVersion, string SchemaVersion, DateTime CreatedUtc,
    string BackupType, string? DatabaseFileName, bool DocumentsIncluded,
    Dictionary<string, string> Checksums);

public sealed class SystemBackupService(IConfiguration configuration)
{
    private readonly string backupRoot = ResolveConfiguredPath(
        configuration["Administration:BackupPath"] ?? "Backups");
    private readonly string documentRoot = ResolveConfiguredPath(
        configuration["Storage:RootPath"] ?? "Documents");
    private readonly string databasePath = ResolveDatabasePath(
        configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=Data/fsm.db");

    public string BackupRoot => backupRoot;
    public string DocumentRoot => documentRoot;
    public string DatabasePath => databasePath;

    public async Task<BackupArtifact> CreateAsync(
        Guid id, string type, CancellationToken ct)
    {
        Directory.CreateDirectory(backupRoot);
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{type.ToLowerInvariant()}-{id:N}.zip";
        var destination = Path.Combine(backupRoot, fileName);
        await using var output = new FileStream(destination, FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, true);
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        if (type is "Database" or "Full")
        {
            var item = await AddDatabaseAsync(archive, ct);
            checksums[item.Key] = item.Value;
        }
        if (type is "Documents" or "Full")
            foreach (var item in await AddDirectoryAsync(
                archive, documentRoot, "Documents", ct))
                checksums[item.Key] = item.Value;
        var manifest = new BackupManifest(SystemVersions.ApplicationVersion,
            await ReadSchemaVersion(ct), DateTime.UtcNow, type,
            type is "Database" or "Full" ? "Database/fsm.db" : null,
            type is "Documents" or "Full", checksums);
        var manifestEntry = archive.CreateEntry("manifest.json");
        await using (var manifestStream = manifestEntry.Open())
            await JsonSerializer.SerializeAsync(manifestStream, manifest,
                cancellationToken: ct);
        await output.FlushAsync(ct);
        return new BackupArtifact(fileName, output.Length);
    }

    public string ResolveBackup(string fileName)
    {
        if (fileName != Path.GetFileName(fileName))
            throw new InvalidOperationException("Invalid backup filename.");
        var full = Path.GetFullPath(Path.Combine(backupRoot, fileName));
        if (!full.StartsWith(
            Path.GetFullPath(backupRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid backup path.");
        return full;
    }

    public async Task ValidateRestoreAsync(string fileName, CancellationToken ct)
    {
        var path = ResolveBackup(fileName);
        if (!File.Exists(path)) throw new FileNotFoundException();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("Backup archive is empty.");
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (Path.IsPathRooted(entry.FullName) ||
                entry.FullName.Split('/', '\\').Any(x => x == ".."))
                throw new InvalidDataException("Backup contains an unsafe path.");
        }
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("Backup manifest is missing.");
        BackupManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                manifestStream, cancellationToken: ct)
                ?? throw new InvalidDataException("Backup manifest is malformed.");
        if (manifest.BackupType is not ("Database" or "Documents" or "Full"))
            throw new InvalidDataException("Backup type is incompatible.");
        if (string.CompareOrdinal(manifest.SchemaVersion,
            SystemVersions.MaximumSupportedDatabaseVersion) > 0)
            throw new InvalidDataException("Backup schema is newer than this application supports.");
        foreach (var checksum in manifest.Checksums)
        {
            var entry = archive.GetEntry(checksum.Key)
                ?? throw new InvalidDataException($"Backup file '{checksum.Key}' is missing.");
            await using var input = entry.Open();
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
            if (!actual.Equals(checksum.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum mismatch for '{checksum.Key}'.");
        }
        if (manifest.BackupType is "Database" or "Full")
        {
            var database = archive.GetEntry(manifest.DatabaseFileName ?? "")
                ?? throw new InvalidDataException("Backup database is missing.");
            var temporary = Path.Combine(backupRoot, $".validate-{Guid.NewGuid():N}.db");
            try
            {
                await using (var input = database.Open())
                await using (var output = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 81920, true))
                    await input.CopyToAsync(output, ct);
                await using var check = new SqliteConnection(
                    $"Data Source={temporary};Mode=ReadOnly;Pooling=False");
                await check.OpenAsync(ct);
                await using var command = check.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(Convert.ToString(await command.ExecuteScalarAsync(ct)),
                    "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backup database failed integrity check.");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public async Task StageRestoreAsync(string fileName, CancellationToken ct)
    {
        await ValidateRestoreAsync(fileName, ct);
        Directory.CreateDirectory(backupRoot);
        await File.WriteAllTextAsync(
            Path.Combine(backupRoot, "restore.pending"), fileName, ct);
    }

    public async Task ApplyPendingRestoreAsync(CancellationToken ct)
    {
        var marker = Path.Combine(backupRoot, "restore.pending");
        if (!File.Exists(marker)) return;
        var fileName = (await File.ReadAllTextAsync(marker, ct)).Trim();
        await ValidateRestoreAsync(fileName, ct);
        var path = ResolveBackup(fileName);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var database = archive.GetEntry("Database/fsm.db");
        if (database is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            var temporary = $"{databasePath}.restore-{Guid.NewGuid():N}";
            await using (var input = database.Open())
            await using (var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, ct);
            File.Move(temporary, databasePath, true);
        }
        foreach (var entry in archive.Entries.Where(x =>
            x.FullName.StartsWith("Documents/", StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(x.Name)))
        {
            var relative = entry.FullName["Documents/".Length..];
            var destination = Path.GetFullPath(Path.Combine(documentRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(documentRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup contains an unsafe document path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, ct);
        }
        File.Delete(marker);
    }

    private async Task<KeyValuePair<string, string>> AddDatabaseAsync(
        ZipArchive archive, CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("Database file not found.", databasePath);
        var temporary = Path.Combine(backupRoot, $".database-{Guid.NewGuid():N}.db");
        try
        {
            await using (var source = new SqliteConnection(
                $"Data Source={databasePath};Pooling=False"))
            await using (var target = new SqliteConnection(
                $"Data Source={temporary};Pooling=False"))
            {
                await source.OpenAsync(ct);
                await target.OpenAsync(ct);
                source.BackupDatabase(target);
            }
            return await AddFileAsync(archive, temporary, "Database/fsm.db", ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<List<KeyValuePair<string, string>>> AddDirectoryAsync(
        ZipArchive archive, string root,
        string prefix, CancellationToken ct)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (!Directory.Exists(root)) return result;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            result.Add(await AddFileAsync(archive, file, $"{prefix}/{relative}", ct));
        }
        return result;
    }

    private static async Task<KeyValuePair<string, string>> AddFileAsync(
        ZipArchive archive, string source,
        string name, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 81920, true);
        await using var output = entry.Open();
        await input.CopyToAsync(output, ct);
        await using var hashInput = new FileStream(source, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite, 81920, true);
        return KeyValuePair.Create(name,
            Convert.ToHexString(await SHA256.HashDataAsync(hashInput, ct)));
    }

    private async Task<string> ReadSchemaVersion(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;""";
        return Convert.ToString(await command.ExecuteScalarAsync(ct))
            ?? SystemVersions.InitialSchemaVersion;
    }

    private static string ResolveConfiguredPath(string value) =>
        Path.GetFullPath(Path.IsPathRooted(Environment.ExpandEnvironmentVariables(value))
            ? Environment.ExpandEnvironmentVariables(value)
            : Path.Combine(AppContext.BaseDirectory,
                Environment.ExpandEnvironmentVariables(value)));
    private static string ResolveDatabasePath(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return ResolveConfiguredPath(builder.DataSource);
    }
}
