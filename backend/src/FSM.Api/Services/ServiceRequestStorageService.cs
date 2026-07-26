using System.IO.Compression;
using QuestPDF.Infrastructure;

namespace FSM.Api.Services;

public sealed class ServiceRequestStorageService(IConfiguration configuration)
{
    private readonly string root = Path.GetFullPath(configuration["Storage:RootPath"]
        ?? throw new InvalidOperationException("Storage root path is not configured."));
    private readonly long maximumSize = configuration.GetValue<long?>(
        "ServiceRequests:MaximumAttachmentSizeBytes") ?? 25L * 1024 * 1024;

    public async Task<(string Path, string Mime, long Size)> SaveAttachmentAsync(
        Guid requestId, IFormFile file, CancellationToken ct)
    {
        if (file.Length <= 0 || file.Length > maximumSize)
            throw new InvalidDataException($"Attachment must be between 1 byte and {maximumSize} bytes.");
        byte[] bytes;
        await using (var input = file.OpenReadStream())
        await using (var buffer = new MemoryStream())
        { await input.CopyToAsync(buffer, ct); bytes = buffer.ToArray(); }
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var mime = extension switch
        {
            ".pdf" => ValidatePdf(bytes),
            ".jpg" or ".jpeg" => ValidateImage(bytes, "image/jpeg"),
            ".png" => ValidateImage(bytes, "image/png"),
            ".docx" => ValidateOffice(bytes, "word/", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            ".xlsx" => ValidateOffice(bytes, "xl/", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            _ => throw new InvalidDataException("Only PDF, JPEG, PNG, DOCX, and XLSX attachments are allowed.")
        };
        var directory = Path.Combine("ServiceRequests", requestId.ToString("N"), "Attachments");
        Directory.CreateDirectory(SafePath(directory));
        var relative = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}").Replace('\\', '/');
        await File.WriteAllBytesAsync(SafePath(relative), bytes, ct);
        return (relative, mime, bytes.LongLength);
    }

    public async Task<string> SavePdfAsync(Guid requestId, int version,
        byte[] bytes, CancellationToken ct)
    {
        var directory = Path.Combine("ServiceRequests", requestId.ToString("N"),
            "Reports", $"v{version}");
        Directory.CreateDirectory(SafePath(directory));
        var relative = Path.Combine(directory, $"{Guid.NewGuid():N}.pdf").Replace('\\', '/');
        await File.WriteAllBytesAsync(SafePath(relative), bytes, ct);
        return relative;
    }
    public string GetAbsolutePath(string relative) => SafePath(relative);
    public void DeleteIfExists(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return;
        var path = SafePath(relative); if (File.Exists(path)) File.Delete(path);
    }
    private string SafePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains('\0')) throw new InvalidOperationException("Invalid stored path.");
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid stored path.");
        return path;
    }
    private static string ValidatePdf(byte[] bytes) =>
        bytes.AsSpan().StartsWith("%PDF-"u8) ? "application/pdf"
        : throw new InvalidDataException("The PDF content is malformed.");
    private static string ValidateImage(byte[] bytes, string mime)
    {
        try { _ = Image.FromBinaryData(bytes); }
        catch { throw new InvalidDataException("The image content is malformed."); }
        return mime;
    }
    private static string ValidateOffice(byte[] bytes, string folder, string mime)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.GetEntry("[Content_Types].xml") is null ||
                !archive.Entries.Any(x => x.FullName.StartsWith(folder,
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The Office document structure is malformed.");
        }
        catch (InvalidDataException) { throw; }
        catch { throw new InvalidDataException("The Office document structure is malformed."); }
        return mime;
    }
}
