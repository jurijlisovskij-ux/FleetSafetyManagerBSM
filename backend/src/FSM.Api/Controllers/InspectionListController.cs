using System.IO.Compression;
using System.Security.Claims;
using FSM.Api.Services;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public sealed record GenerateInspectionListRequest(
    Guid VesselId,
    IReadOnlyList<Guid> EquipmentIds,
    string Format,
    bool IncludeCertificates);

[ApiController]
[Authorize(Roles = "OfficeAdmin,Superintendent,Crew")]
[Route("api/inspection-list")]
public sealed class InspectionListController(
    FsmDbContext db,
    InspectionListService inspectionListService,
    DocumentStorageService storage) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Generate(
        GenerateInspectionListRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EquipmentIds.Count == 0)
        {
            return BadRequest(new
            {
                message =
                    "Select at least one equipment item."
            });
        }

        if (!string.Equals(
                request.Format,
                "excel",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                request.Format,
                "pdf",
                StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                message =
                    "Format must be excel or pdf."
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

        var selectedIds = request.EquipmentIds
            .Distinct()
            .ToList();

        var equipment = await db.Equipment
            .AsNoTracking()
            .Where(x =>
                x.VesselId == request.VesselId &&
                selectedIds.Contains(x.Id))
            .OrderBy(x => x.Category)
            .ThenBy(x => x.EquipmentName)
            .ThenBy(x => x.Maker)
            .ThenBy(x => x.Model)
            .ToListAsync(cancellationToken);

        if (equipment.Count != selectedIds.Count)
        {
            return BadRequest(new
            {
                message =
                    "One or more selected equipment items are invalid."
            });
        }

        var isPdf = string.Equals(
            request.Format,
            "pdf",
            StringComparison.OrdinalIgnoreCase);

        var listFile = isPdf
            ? inspectionListService.GeneratePrintablePdf(
                vessel,
                equipment)
            : inspectionListService.GenerateExcel(
                vessel,
                equipment);

        var extension = isPdf ? ".pdf" : ".xlsx";
        var contentType = isPdf
            ? "application/pdf"
            : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        var safeVesselName = string.Concat(
            vessel.VesselName.Select(character =>
                Path.GetInvalidFileNameChars()
                    .Contains(character)
                    ? '_'
                    : character));

        var dateText =
            DateTime.UtcNow.ToString("yyyy-MM-dd");

        var downloadName =
            $"Inspection List - {safeVesselName} - " +
            $"{dateText}{extension}";

        if (!request.IncludeCertificates)
        {
            return File(
                listFile,
                contentType,
                downloadName);
        }

        await using var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(
            zipStream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            var listEntry = archive.CreateEntry(
                $"Inspection List{extension}",
                CompressionLevel.Optimal);

            await using (var output = listEntry.Open())
            {
                await output.WriteAsync(
                    listFile,
                    cancellationToken);
            }

            foreach (var item in equipment)
            {
                if (string.IsNullOrWhiteSpace(
                    item.CurrentCertificatePath))
                {
                    continue;
                }

                AddCertificateToArchive(
                    archive,
                    storage,
                    item);
            }
        }

        return File(
            zipStream.ToArray(),
            "application/zip",
            $"Inspection List - {safeVesselName} - " +
            $"{dateText}.zip");
    }

    private static void AddCertificateToArchive(
        ZipArchive archive,
        DocumentStorageService storage,
        FSM.Domain.Entities.Equipment equipment)
    {
        var relativePath =
            equipment.CurrentCertificatePath;

        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var absolutePath =
            storage.GetAbsolutePath(relativePath);

        if (!System.IO.File.Exists(absolutePath))
            return;

        var safeEquipmentName = string.Concat(
            equipment.EquipmentName.Select(character =>
                Path.GetInvalidFileNameChars()
                    .Contains(character)
                    ? '_'
                    : character));

        var originalName =
            equipment.CurrentCertificateFileName;

        var extension =
            Path.GetExtension(originalName);

        if (string.IsNullOrWhiteSpace(extension))
            extension = ".pdf";

        var entry = archive.CreateEntry(
            $"Certificates/" +
            $"{safeEquipmentName}_{equipment.Id:N}" +
            $"{extension}",
            CompressionLevel.Optimal);

        using var input =
            System.IO.File.OpenRead(absolutePath);

        using var output = entry.Open();
        input.CopyTo(output);
    }

    private async Task<bool> CanAccessVesselAsync(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var role =
            User.FindFirstValue(ClaimTypes.Role);

        if (string.Equals(
            role,
            "OfficeAdmin",
            StringComparison.Ordinal))
        {
            return true;
        }

        var userIdText =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(
            userIdText,
            out var userId))
        {
            return false;
        }

        return await db.UserVessels
            .AsNoTracking()
            .AnyAsync(
                x => x.UserId == userId &&
                     x.VesselId == vesselId,
                cancellationToken);
    }
}
