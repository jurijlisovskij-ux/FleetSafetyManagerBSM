using System.Text;

namespace FSM.Api.Services;

public sealed class DocumentStorageService(IConfiguration configuration)
{
    private const long MaximumFileSize = 25L * 1024L * 1024L;

    private readonly string rootPath =
        configuration["Storage:RootPath"]
        ?? throw new InvalidOperationException(
            "Storage root path is not configured.");

    public async Task<string> SavePdfAsync(
        string documentType,
        string imoNumber,
        Guid equipmentId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        Validate(file);

        var folderName = documentType switch
        {
            "report" => "reports",
            "certificate" => "certificates",
            _ => throw new ArgumentOutOfRangeException(nameof(documentType))
        };

        var relativeDirectory = Path.Combine(folderName, imoNumber);
        var absoluteDirectory = Path.Combine(rootPath, relativeDirectory);
        Directory.CreateDirectory(absoluteDirectory);

        var finalName = $"{equipmentId:N}.pdf";
        var finalPath = Path.Combine(absoluteDirectory, finalName);
        var temporaryPath = Path.Combine(
            absoluteDirectory,
            $"{equipmentId:N}.{Guid.NewGuid():N}.tmp");

        await using (var output = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true))
        {
            await file.CopyToAsync(output, cancellationToken);
        }

        try
        {
            await using var input = File.OpenRead(temporaryPath);
            var signature = new byte[5];
            var bytesRead = await input.ReadAsync(
                signature,
                cancellationToken);

            if (bytesRead != 5 ||
                Encoding.ASCII.GetString(signature) != "%PDF-")
            {
                throw new InvalidDataException(
                    "The selected file is not a valid PDF.");
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }

        return Path.Combine(relativeDirectory, finalName)
            .Replace("\\", "/");
    }

    public string GetAbsolutePath(string relativePath)
    {
        var absoluteRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var absolutePath = Path.GetFullPath(
            Path.Combine(rootPath, relativePath));

        if (!absolutePath.StartsWith(
            absoluteRoot,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Invalid document path.");
        }

        return absolutePath;
    }

    public void DeleteIfExists(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var absolutePath = GetAbsolutePath(relativePath);

        if (File.Exists(absolutePath))
            File.Delete(absolutePath);
    }

    private static void Validate(IFormFile file)
    {
        if (file.Length <= 0)
            throw new InvalidDataException(
                "The selected file is empty.");

        if (file.Length > MaximumFileSize)
            throw new InvalidDataException(
                "PDF file size must not exceed 25 MB.");

        if (!string.Equals(
            Path.GetExtension(file.FileName),
            ".pdf",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Only PDF files are allowed.");
        }
    }
}
