using System.Security.Cryptography;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Synchronization;

public sealed class SynchronizationFileService
{
    private static readonly HashSet<string> AllowedFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "reports",
            "certificates",
            "service-requests"
        };

    private readonly FsmDbContext db;
    private readonly SynchronizationOptions options;
    private readonly string storageRoot;
    private readonly string transferRoot;
    private readonly string downloadRoot;

    public SynchronizationFileService(
        FsmDbContext db,
        IConfiguration configuration,
        IOptions<SynchronizationOptions> options)
    {
        this.db = db;
        this.options = options.Value;
        storageRoot = Path.GetFullPath(
            configuration["Storage:RootPath"] ?? "Documents");
        transferRoot = Path.GetFullPath(
            configuration["Synchronization:TransferRootPath"] ??
            Path.Combine("Data", "SynchronizationTransfers"));
        downloadRoot = Path.GetFullPath(
            configuration["Synchronization:DownloadRootPath"] ??
            Path.Combine("Data", "SynchronizationDownloads"));
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(transferRoot);
        Directory.CreateDirectory(downloadRoot);
    }

    public async Task<IReadOnlyList<SynchronizationFileItem>> GetChangedFilesAsync(
        Vessel vessel,
        DateTime sinceUtc,
        DateTime snapshotUtc,
        CancellationToken cancellationToken)
    {
        var files = EnumerateVesselFiles(vessel)
            .Where(file =>
                file.LastWriteTimeUtc > sinceUtc &&
                file.LastWriteTimeUtc <= snapshotUtc)
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new List<SynchronizationFileItem>(files.Count);

        foreach (var file in files)
        {
            result.Add(new SynchronizationFileItem(
                ToRelativePath(file.FullName),
                file.Length,
                await ComputeChecksumAsync(
                    file.FullName,
                    cancellationToken),
                file.LastWriteTimeUtc));
        }

        return result;
    }

    public int CountChangedFiles(Vessel vessel, DateTime sinceUtc) =>
        EnumerateVesselFiles(vessel)
            .Count(file => file.LastWriteTimeUtc > sinceUtc);

    public async Task<PrepareSynchronizationFileResponse> PrepareAsync(
        Vessel vessel,
        SynchronizationFileItem file,
        CancellationToken cancellationToken)
    {
        ValidateMetadata(file);
        var destination = GetAbsolutePath(vessel, file.RelativePath);

        if (File.Exists(destination) &&
            string.Equals(
                await ComputeChecksumAsync(destination, cancellationToken),
                file.Checksum,
                StringComparison.OrdinalIgnoreCase))
        {
            return new PrepareSynchronizationFileResponse(
                null,
                file.Size,
                true);
        }

        var transfer = await db.SynchronizationTransfers
            .SingleOrDefaultAsync(
                x => x.VesselId == vessel.Id &&
                     x.RelativePath == Normalize(file.RelativePath) &&
                     x.Checksum == file.Checksum,
                cancellationToken);

        if (transfer is null)
        {
            var id = Guid.NewGuid();
            transfer = new SynchronizationTransfer
            {
                Id = id,
                VesselId = vessel.Id,
                RelativePath = Normalize(file.RelativePath),
                Checksum = file.Checksum.ToUpperInvariant(),
                TotalBytes = file.Size,
                TemporaryPath = Path.Combine(
                    transferRoot,
                    $"{id:N}.part"),
                FileLastModifiedUtc = file.LastModifiedUtc,
                UpdatedUtc = DateTime.UtcNow
            };
            db.SynchronizationTransfers.Add(transfer);
        }

        transfer.ReceivedBytes = File.Exists(transfer.TemporaryPath)
            ? new FileInfo(transfer.TemporaryPath).Length
            : 0;

        if (transfer.ReceivedBytes > transfer.TotalBytes)
            throw new InvalidDataException("The partial upload is invalid.");

        transfer.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new PrepareSynchronizationFileResponse(
            transfer.Id,
            transfer.ReceivedBytes,
            false);
    }

    public async Task<long> AppendAsync(
        SynchronizationTransfer transfer,
        long offset,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (offset != transfer.ReceivedBytes)
            throw new InvalidDataException(
                "The upload offset does not match the saved position.");

        Directory.CreateDirectory(transferRoot);
        await using var output = new FileStream(
            transfer.TemporaryPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            options.ChunkSizeBytes,
            useAsync: true);
        output.Position = offset;

        var buffer = new byte[options.ChunkSizeBytes];
        var totalRead = 0;
        while (true)
        {
            var read = await content.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
                break;
            totalRead += read;
            if (totalRead > options.ChunkSizeBytes ||
                offset + totalRead > transfer.TotalBytes)
            {
                throw new InvalidDataException(
                    "The synchronization chunk is too large.");
            }
            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        transfer.ReceivedBytes = offset + totalRead;
        transfer.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return transfer.ReceivedBytes;
    }

    public async Task CompleteAsync(
        Vessel vessel,
        SynchronizationTransfer transfer,
        CancellationToken cancellationToken)
    {
        if (transfer.ReceivedBytes != transfer.TotalBytes ||
            !File.Exists(transfer.TemporaryPath))
        {
            throw new InvalidDataException(
                "The upload has not completed.");
        }

        var checksum = await ComputeChecksumAsync(
            transfer.TemporaryPath,
            cancellationToken);
        if (!string.Equals(
            checksum,
            transfer.Checksum,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The uploaded file checksum is invalid.");
        }

        var destination = GetAbsolutePath(
            vessel,
            transfer.RelativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)!);
        File.Move(transfer.TemporaryPath, destination, overwrite: true);
        File.SetLastWriteTimeUtc(
            destination,
            transfer.FileLastModifiedUtc);
        db.SynchronizationTransfers.Remove(transfer);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<byte[]> ReadChunkAsync(
        Vessel vessel,
        SynchronizationFileItem file,
        long offset,
        CancellationToken cancellationToken)
    {
        ValidateMetadata(file);
        var path = GetAbsolutePath(vessel, file.RelativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("Synchronization file not found.");
        if (offset < 0 || offset > file.Size)
            throw new InvalidDataException("Invalid download offset.");

        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            options.ChunkSizeBytes,
            useAsync: true);
        input.Position = offset;
        var count = (int)Math.Min(
            options.ChunkSizeBytes,
            file.Size - offset);
        var buffer = new byte[count];
        var read = await input.ReadAsync(buffer, cancellationToken);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    public async Task<bool> IsCurrentAsync(
        Vessel vessel,
        SynchronizationFileItem file,
        CancellationToken cancellationToken)
    {
        var path = GetAbsolutePath(vessel, file.RelativePath);
        return File.Exists(path) &&
            string.Equals(
                await ComputeChecksumAsync(path, cancellationToken),
                file.Checksum,
                StringComparison.OrdinalIgnoreCase);
    }

    public long GetDownloadOffset(
        Guid vesselId,
        SynchronizationFileItem file)
    {
        var path = GetDownloadPath(vesselId, file);
        if (!File.Exists(path))
            return 0;
        var length = new FileInfo(path).Length;
        if (length <= file.Size)
            return length;
        File.Delete(path);
        return 0;
    }

    public async Task AppendDownloadAsync(
        Guid vesselId,
        SynchronizationFileItem file,
        long offset,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var path = GetDownloadPath(vesselId, file);
        Directory.CreateDirectory(downloadRoot);
        await using var output = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            options.ChunkSizeBytes,
            useAsync: true);
        if (output.Length != offset ||
            offset + content.LongLength > file.Size)
        {
            throw new InvalidDataException(
                "The downloaded file offset is invalid.");
        }
        output.Position = offset;
        await output.WriteAsync(content, cancellationToken);
    }

    public async Task CompleteDownloadAsync(
        Vessel vessel,
        SynchronizationFileItem file,
        CancellationToken cancellationToken)
    {
        var temporaryPath = GetDownloadPath(vessel.Id, file);
        if (!File.Exists(temporaryPath) ||
            new FileInfo(temporaryPath).Length != file.Size)
        {
            throw new InvalidDataException(
                "The downloaded file is incomplete.");
        }

        var checksum = await ComputeChecksumAsync(
            temporaryPath,
            cancellationToken);
        if (!string.Equals(
            checksum,
            file.Checksum,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The downloaded file checksum is invalid.");
        }

        var destination = GetAbsolutePath(vessel, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(temporaryPath, destination, overwrite: true);
        File.SetLastWriteTimeUtc(destination, file.LastModifiedUtc);
    }

    public string GetAbsolutePath(Vessel vessel, string relativePath)
    {
        var normalized = Normalize(relativePath);
        var segments = normalized.Split('/');
        if (segments.Length < 3 ||
            !AllowedFolders.Contains(segments[0]) ||
            !segments[1].Equals(
                vessel.ImoNumber,
                StringComparison.OrdinalIgnoreCase) ||
            segments.Skip(2).Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".."))
        {
            throw new InvalidDataException(
                "The synchronization file path is not valid for this vessel.");
        }

        var vesselRoot = Path.GetFullPath(Path.Combine(
            storageRoot,
            segments[0],
            segments[1]));
        var rootPrefix = vesselRoot
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var absolute = Path.GetFullPath(
            Path.Combine(storageRoot, normalized));
        if (!absolute.StartsWith(
            rootPrefix,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The synchronization file path escapes document storage.");
        }
        return absolute;
    }

    private IEnumerable<FileInfo> EnumerateVesselFiles(Vessel vessel)
    {
        foreach (var folder in AllowedFolders)
        {
            var path = Path.Combine(storageRoot, folder, vessel.ImoNumber);
            if (!Directory.Exists(path))
                continue;
            foreach (var file in new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Extension.Equals(
                    ".tmp",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                yield return file;
            }
        }
    }

    private string ToRelativePath(string absolutePath) =>
        Normalize(Path.GetRelativePath(storageRoot, absolutePath));

    private string GetDownloadPath(
        Guid vesselId,
        SynchronizationFileItem file) =>
        Path.Combine(
            downloadRoot,
            $"{vesselId:N}_{file.Checksum.ToUpperInvariant()}.part");

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private void ValidateMetadata(SynchronizationFileItem file)
    {
        if (file.Size < 0 || file.Size > options.MaximumFileSizeBytes)
            throw new InvalidDataException(
                $"The synchronization file exceeds the maximum allowed size of {options.MaximumFileSizeBytes} bytes.");
        if (file.Checksum.Length != 64 ||
            !file.Checksum.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "Invalid synchronization file checksum.");
        }
    }

    private static async Task<string> ComputeChecksumAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken);
        return Convert.ToHexString(hash);
    }
}
