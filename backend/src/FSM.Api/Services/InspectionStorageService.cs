namespace FSM.Api.Services;

public sealed class InspectionStorageService(IConfiguration configuration)
{
    private readonly string root = Path.GetFullPath(
        configuration["Storage:RootPath"] ??
        throw new InvalidOperationException("Storage root path is not configured."));
    private readonly long maximumPhotoSize =
        configuration.GetValue<long?>("Inspections:MaximumPhotoSizeBytes")
        ?? 15L * 1024 * 1024;

    public async Task<(string Path, string MimeType, long Size)> SavePhotoAsync(
        Guid reportId, Guid findingId, IFormFile file, CancellationToken ct)
    {
        if (file.Length <= 0 || file.Length > maximumPhotoSize)
            throw new InvalidDataException(
                $"Photo must be between 1 byte and {maximumPhotoSize} bytes.");

        byte[] content;
        await using (var input = file.OpenReadStream())
        await using (var buffer = new MemoryStream())
        {
            await input.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }
        var header = content.AsSpan(0, Math.Min(8, content.Length));
        var jpeg = header.Length >= 3 &&
            header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
        var png = header.Length == 8 &&
            header.SequenceEqual(new byte[] {137,80,78,71,13,10,26,10});
        if (!jpeg && !png)
            throw new InvalidDataException("Only valid JPEG and PNG images are allowed.");
        try
        {
            _ = QuestPDF.Infrastructure.Image.FromBinaryData(content);
        }
        catch
        {
            throw new InvalidDataException("The uploaded image is malformed.");
        }

        var extension = jpeg ? ".jpg" : ".png";
        var relativeDirectory = Path.Combine("Inspections", reportId.ToString("N"),
            "Findings", findingId.ToString("N"), "Photos");
        var directory = SafePath(relativeDirectory);
        Directory.CreateDirectory(directory);
        var relative = Path.Combine(relativeDirectory, $"{Guid.NewGuid():N}{extension}")
            .Replace('\\', '/');
        var absolute = SafePath(relative);
        await File.WriteAllBytesAsync(absolute, content, ct);
        return (relative, jpeg ? "image/jpeg" : "image/png", file.Length);
    }

    public async Task<string> SavePdfAsync(
        Guid reportId, int version, byte[] content, CancellationToken ct)
    {
        var directory = Path.Combine("Inspections", reportId.ToString("N"),
            "Reports", $"v{version}");
        Directory.CreateDirectory(SafePath(directory));
        var relative = Path.Combine(directory, $"{Guid.NewGuid():N}.pdf").Replace('\\', '/');
        await File.WriteAllBytesAsync(SafePath(relative), content, ct);
        return relative;
    }

    public string GetAbsolutePath(string relativePath) => SafePath(relativePath);
    public void DeleteIfExists(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        var path = SafePath(relativePath);
        if (File.Exists(path)) File.Delete(path);
    }

    private string SafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\0'))
            throw new InvalidOperationException("Invalid stored path.");
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(rootPrefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid stored path.");
        return path;
    }
}
