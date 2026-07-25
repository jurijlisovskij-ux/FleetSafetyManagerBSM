using System.Security.Claims;
using FSM.Api.Services;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/documents")]
public sealed class DocumentRepositoryController(
    FsmDbContext db,
    DocumentStorageService storage) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetMetadata(
        Guid id,
        CancellationToken cancellationToken)
    {
        var document = await db.EquipmentDocuments
            .AsNoTracking()
            .Include(x => x.Equipment)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (document?.Equipment is null)
            return NotFound();

        if (!await CanAccessVesselAsync(
            document.Equipment.VesselId,
            cancellationToken))
        {
            return Forbid();
        }

        return Ok(new
        {
            document.Id,
            document.EquipmentId,
            Type = document.DocumentType.ToString(),
            document.FileName,
            document.MimeType,
            document.FileSize,
            document.IssueDate,
            document.ExpiryDate,
            document.DocumentNumber,
            document.Issuer,
            document.Remarks,
            document.IsCurrent,
            document.CreatedUtc,
            document.ArchivedUtc
        });
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(
        Guid id,
        CancellationToken cancellationToken)
    {
        var document = await db.EquipmentDocuments
            .AsNoTracking()
            .Include(x => x.Equipment)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (document?.Equipment is null)
            return NotFound();

        if (!await CanAccessVesselAsync(
            document.Equipment.VesselId,
            cancellationToken))
        {
            return Forbid();
        }

        var absolutePath = storage.GetAbsolutePath(
            document.StoredFileName);

        if (!System.IO.File.Exists(absolutePath))
            return NotFound();

        return PhysicalFile(
            absolutePath,
            document.MimeType,
            document.FileName,
            enableRangeProcessing: true);
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
