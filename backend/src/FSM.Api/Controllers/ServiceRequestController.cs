using System.IO.Compression;
using FSM.Api.Authorization;
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
    ServiceRequestExcelService excelService,
    DocumentStorageService storage,
    VesselAccessService vesselAccess) : ControllerBase
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

        if (!await vesselAccess.CanAccessVesselAsync(
            User,
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


        var excelFile = excelService.Generate(
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
        var excelFileName =
            $"Service Request - {safeVesselName} - {dateText}.xlsx";

        await storage.SaveServiceRequestAsync(
            vessel.ImoNumber,
            excelFile,
            cancellationToken);

        if (!request.IncludeCertificates &&
            !request.IncludeReports)
        {
            return File(
                excelFile,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                excelFileName);
        }

        await using var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(
            zipStream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            var excelEntry = archive.CreateEntry(
                "Service Request.xlsx",
                CompressionLevel.Optimal);

            await using (var entryStream = excelEntry.Open())
            {
                await entryStream.WriteAsync(
                    excelFile,
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

}
