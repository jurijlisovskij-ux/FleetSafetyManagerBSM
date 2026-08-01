using System.IO.Compression;
using System.Security.Claims;
using FSM.Api.Services;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public sealed record GenerateServiceRequestRequest(
    Guid VesselId,
    IReadOnlyList<Guid> EquipmentIds,
    string? Port,
    DateOnly? Eta,
    DateOnly? Etd,
    string? Remarks,
    bool IncludeCertificates,
    bool IncludeReports);

[ApiController]
[Authorize(Roles = "OfficeAdmin,Superintendent,Crew")]
[Route("api/service-request")]
public sealed class ServiceRequestController(
    FsmDbContext db,
    ServiceRequestPdfService pdfService,
    DocumentStorageService storage) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Generate(
        GenerateServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EquipmentIds.Count == 0)
        {
            return BadRequest(new
            {
                message = "Select at least one equipment item."
            });
        }

        if (!await CanAccessVesselAsync(
            request.VesselId,
            cancellationToken))
        {
            return Forbid();
        }

        var vessel = await db.Vessels
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == request.VesselId,
                cancellationToken);

        if (vessel is null)
            return NotFound();

        var distinctIds = request.EquipmentIds
            .Distinct()
            .ToList();

        var equipment = await db.Equipment
            .AsNoTracking()
            .Where(x =>
                x.VesselId == request.VesselId &&
                distinctIds.Contains(x.Id))
            .OrderBy(x => x.Category)
            .ThenBy(x => x.EquipmentName)
            .ThenBy(x => x.Maker)
            .ThenBy(x => x.Model)
            .ToListAsync(cancellationToken);

        if (equipment.Count != distinctIds.Count)
        {
            return BadRequest(new
            {
                message =
                    "One or more selected equipment items are invalid."
            });
        }

        var pdfFile = pdfService.Generate(
            vessel,
            equipment,
            (request.Port ?? string.Empty).Trim(),
            request.Eta,
            request.Etd,
            request.Remarks?.Trim());

        var safeVesselName = string.Concat(
            vessel.VesselName.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character)
                    ? '_'
                    : character));

        var dateText = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var pdfFileName =
            $"Service Request - {safeVesselName} - {dateText}.pdf";

        if (!request.IncludeCertificates &&
            !request.IncludeReports)
        {
            return File(
                pdfFile,
                "application/pdf",
                pdfFileName);
        }

        await using var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(
            zipStream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            var pdfEntry = archive.CreateEntry(
                "Service Request.pdf",
                CompressionLevel.Optimal);

            await using (var entryStream = pdfEntry.Open())
            {
                await entryStream.WriteAsync(
                    pdfFile,
                    cancellationToken);
            }

            foreach (var item in equipment)
            {
                if (request.IncludeCertificates &&
                    !string.IsNullOrWhiteSpace(
                        item.CurrentCertificatePath))
                {
                    AddDocumentToArchive(
                        archive,
                        storage,
                        item,
                        item.CurrentCertificatePath,
                        item.CurrentCertificateFileName,
                        "Certificates");
                }

                if (request.IncludeReports &&
                    !string.IsNullOrWhiteSpace(
                        item.CurrentReportPath))
                {
                    AddDocumentToArchive(
                        archive,
                        storage,
                        item,
                        item.CurrentReportPath,
                        item.CurrentReportFileName,
                        "Reports");
                }
            }
        }

        var zipFileName =
            $"Service Request - {safeVesselName} - {dateText}.zip";

        return File(
            zipStream.ToArray(),
            "application/zip",
            zipFileName);
    }

    private static void AddDocumentToArchive(
        ZipArchive archive,
        DocumentStorageService storage,
        FSM.Domain.Entities.Equipment equipment,
        string relativePath,
        string? originalFileName,
        string folderName)
    {
        var absolutePath = storage.GetAbsolutePath(relativePath);

        if (!System.IO.File.Exists(absolutePath))
            return;

        var safeEquipmentName = string.Concat(
            equipment.EquipmentName.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character)
                    ? '_'
                    : character));

        var sourceFileName = string.IsNullOrWhiteSpace(originalFileName)
            ? Path.GetFileName(absolutePath)
            : Path.GetFileName(originalFileName);

        var extension = Path.GetExtension(sourceFileName);

        if (string.IsNullOrWhiteSpace(extension))
            extension = ".pdf";

        var entryName =
            $"{folderName}/{safeEquipmentName}_{equipment.Id:N}{extension}";

        var entry = archive.CreateEntry(
            entryName,
            CompressionLevel.Optimal);

        using var input = System.IO.File.OpenRead(absolutePath);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private async Task<bool> CanAccessVesselAsync(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var role = User.FindFirstValue(ClaimTypes.Role);

        if (string.Equals(
            role,
            "OfficeAdmin",
            StringComparison.Ordinal))
        {
            return true;
        }

        var userIdText =
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(userIdText, out var userId))
            return false;

        return await db.UserVessels
            .AsNoTracking()
            .AnyAsync(
                x => x.UserId == userId &&
                     x.VesselId == vesselId,
                cancellationToken);
    }
}
