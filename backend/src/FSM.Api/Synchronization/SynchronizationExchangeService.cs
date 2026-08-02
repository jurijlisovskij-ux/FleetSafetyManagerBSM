using FSM.Api.Services;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Synchronization;

public sealed class SynchronizationExchangeService(
    FsmDbContext db,
    SynchronizationFileService files,
    DocumentStorageService documentStorage,
    IConfiguration configuration)
{
    private readonly string localEdition =
        configuration["Application:Edition"] ?? "Office";

    public async Task<SynchronizationPayload> BuildPayloadAsync(
        Guid vesselId,
        DateTime sinceUtc,
        DateTime snapshotUtc,
        CancellationToken cancellationToken)
    {
        var vessel = await db.Vessels
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(x => x.Id == vesselId, cancellationToken);

        var vessels = await db.Vessels
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x =>
                x.Id == vesselId &&
                x.LastModifiedUtc > sinceUtc &&
                x.LastModifiedUtc <= snapshotUtc)
            .Select(x => new VesselSynchronizationItem(
                x.Id,
                x.VesselName,
                x.ImoNumber,
                x.Flag,
                x.CompanyName,
                x.InvoicingDetails,
                x.AccountingEmail,
                x.LastModifiedUtc,
                x.IsDeleted))
            .ToListAsync(cancellationToken);

        var equipment = await db.Equipment
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x =>
                x.VesselId == vesselId &&
                x.LastModifiedUtc > sinceUtc &&
                x.LastModifiedUtc <= snapshotUtc)
            .Select(x => new EquipmentSynchronizationItem(
                x.Id,
                x.VesselId,
                x.Category,
                x.EquipmentName,
                x.Maker,
                x.Model,
                x.SerialNumber,
                x.Quantity,
                x.InspectionBy,
                x.ExpiryDate,
                x.Responsible,
                x.Remarks,
                x.CurrentReportPath,
                x.CurrentReportFileName,
                x.CurrentCertificatePath,
                x.CurrentCertificateFileName,
                x.LastModifiedUtc,
                x.IsDeleted))
            .ToListAsync(cancellationToken);

        var allAssignments = await db.UserVessels
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.VesselId == vesselId)
            .ToListAsync(cancellationToken);
        var changedAssignments = allAssignments
            .Where(x =>
                x.LastModifiedUtc > sinceUtc &&
                x.LastModifiedUtc <= snapshotUtc)
            .ToList();
        var changedAssignmentUserIds = changedAssignments
            .Select(x => x.UserId)
            .ToHashSet();
        var historicalUserIds = allAssignments
            .Select(x => x.UserId)
            .Distinct()
            .ToList();

        var users = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x =>
                historicalUserIds.Contains(x.Id) &&
                ((x.LastModifiedUtc > sinceUtc &&
                  x.LastModifiedUtc <= snapshotUtc) ||
                 changedAssignmentUserIds.Contains(x.Id)))
            .Select(x => new UserSynchronizationItem(
                x.Id,
                x.FullName,
                x.Email,
                x.PasswordHash,
                x.Role,
                x.IsActive,
                x.LastModifiedUtc,
                x.IsDeleted))
            .ToListAsync(cancellationToken);

        var userVessels = changedAssignments
            .Select(x => new UserVesselSynchronizationItem(
                x.UserId,
                x.VesselId,
                x.LastModifiedUtc,
                x.IsDeleted))
            .ToList();

        var vesselSettings = new List<VesselSettingsSynchronizationItem>();
        if (localEdition.Equals("Office", StringComparison.OrdinalIgnoreCase))
        {
            var setting = await db.SynchronizationSettings
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.VesselId == vesselId &&
                         x.LastModifiedUtc > sinceUtc &&
                         x.LastModifiedUtc <= snapshotUtc,
                    cancellationToken);
            if (setting is not null)
            {
                vesselSettings.Add(
                    new VesselSettingsSynchronizationItem(
                        setting.VesselId,
                        setting.AutomaticIntervalHours,
                        setting.LastModifiedUtc));
            }
        }

        var changedFiles = vessel.IsDeleted
            ? Array.Empty<SynchronizationFileItem>()
            : await files.GetChangedFilesAsync(
                vessel,
                sinceUtc,
                snapshotUtc,
                cancellationToken);

        return new SynchronizationPayload(
            vessels,
            equipment,
            users,
            userVessels,
            vesselSettings,
            changedFiles);
    }

    public async Task<(int Accepted, int Conflicts, List<string> Warnings)>
        ApplyAsync(
            SynchronizationPayload payload,
            Guid vesselId,
            DateTime sourceRemoteCursorUtc,
            string sourceEdition,
            CancellationToken cancellationToken)
    {
        var accepted = 0;
        var conflicts = 0;
        var warnings = new List<string>();

        foreach (var incoming in payload.Vessels.Where(x => x.Id == vesselId))
        {
            var existing = await db.Vessels
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.Id == incoming.Id, cancellationToken);
            if (!ShouldApply(
                existing is not null,
                sourceEdition,
                "Office",
                "vessel",
                incoming.Id,
                warnings,
                ref conflicts))
            {
                continue;
            }

            existing ??= new Vessel
            {
                Id = incoming.Id,
                VesselName = incoming.VesselName,
                ImoNumber = incoming.ImoNumber
            };
            if (db.Entry(existing).State == EntityState.Detached)
                db.Vessels.Add(existing);
            existing.VesselName = incoming.VesselName;
            existing.ImoNumber = incoming.ImoNumber;
            existing.Flag = incoming.Flag;
            existing.CompanyName = incoming.CompanyName;
            existing.InvoicingDetails = incoming.InvoicingDetails;
            existing.AccountingEmail = incoming.AccountingEmail;
            existing.LastModifiedUtc = EnsureUtc(incoming.LastModifiedUtc);
            existing.IsDeleted = incoming.IsDeleted;
            accepted++;
        }

        foreach (var incoming in payload.Equipment.Where(x => x.VesselId == vesselId))
        {
            var existing = await db.Equipment
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.Id == incoming.Id, cancellationToken);
            existing ??= await FindMatchingEquipmentAsync(
                incoming,
                cancellationToken);
            if (!ShouldApply(
                existing is not null,
                sourceEdition,
                "Ship",
                "equipment",
                incoming.Id,
                warnings,
                ref conflicts))
            {
                continue;
            }

            var oldReport = existing?.CurrentReportPath;
            var oldCertificate = existing?.CurrentCertificatePath;
            existing ??= new Equipment
            {
                Id = incoming.Id,
                VesselId = vesselId,
                EquipmentName = incoming.EquipmentName
            };
            if (db.Entry(existing).State == EntityState.Detached)
                db.Equipment.Add(existing);
            existing.Category = incoming.Category;
            existing.EquipmentName = incoming.EquipmentName;
            existing.Maker = incoming.Maker;
            existing.Model = incoming.Model;
            existing.SerialNumber = incoming.SerialNumber;
            existing.Quantity = incoming.Quantity;
            existing.InspectionBy = incoming.InspectionBy;
            existing.ExpiryDate = incoming.ExpiryDate;
            existing.Responsible = incoming.Responsible;
            existing.Remarks = incoming.Remarks;
            existing.CurrentReportPath = incoming.CurrentReportPath;
            existing.CurrentReportFileName = incoming.CurrentReportFileName;
            existing.CurrentCertificatePath = incoming.CurrentCertificatePath;
            existing.CurrentCertificateFileName = incoming.CurrentCertificateFileName;
            existing.LastModifiedUtc = EnsureUtc(incoming.LastModifiedUtc);
            existing.IsDeleted = incoming.IsDeleted;

            if (incoming.IsDeleted ||
                !string.Equals(oldReport, incoming.CurrentReportPath, StringComparison.Ordinal))
            {
                documentStorage.DeleteIfExists(oldReport);
            }
            if (incoming.IsDeleted ||
                !string.Equals(oldCertificate, incoming.CurrentCertificatePath, StringComparison.Ordinal))
            {
                documentStorage.DeleteIfExists(oldCertificate);
            }
            accepted++;
        }

        foreach (var incoming in payload.Users)
        {
            var existing = await db.Users
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.Id == incoming.Id, cancellationToken);
            if (!ShouldApply(
                existing is not null,
                sourceEdition,
                "Office",
                "user",
                incoming.Id,
                warnings,
                ref conflicts))
            {
                continue;
            }

            existing ??= new User
            {
                Id = incoming.Id,
                FullName = incoming.FullName,
                Email = incoming.Email,
                PasswordHash = incoming.PasswordHash
            };
            if (db.Entry(existing).State == EntityState.Detached)
                db.Users.Add(existing);
            existing.FullName = incoming.FullName;
            existing.Email = incoming.Email;
            existing.PasswordHash = incoming.PasswordHash;
            existing.Role = incoming.Role;
            existing.IsActive = incoming.IsActive;
            existing.LastModifiedUtc = EnsureUtc(incoming.LastModifiedUtc);
            existing.IsDeleted = incoming.IsDeleted;
            accepted++;
        }

        foreach (var incoming in payload.UserVessels.Where(x => x.VesselId == vesselId))
        {
            var existing = await db.UserVessels
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    x => x.UserId == incoming.UserId &&
                         x.VesselId == incoming.VesselId,
                    cancellationToken);
            if (!ShouldApply(
                existing is not null,
                sourceEdition,
                "Office",
                "user assignment",
                incoming.UserId,
                warnings,
                ref conflicts))
            {
                continue;
            }

            existing ??= new UserVessel
            {
                UserId = incoming.UserId,
                VesselId = incoming.VesselId
            };
            if (db.Entry(existing).State == EntityState.Detached)
                db.UserVessels.Add(existing);
            existing.LastModifiedUtc = EnsureUtc(incoming.LastModifiedUtc);
            existing.IsDeleted = incoming.IsDeleted;
            accepted++;
        }

        if (localEdition.Equals("Ship", StringComparison.OrdinalIgnoreCase) &&
            sourceEdition.Equals("Office", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var incoming in payload.VesselSettings
                .Where(x => x.VesselId == vesselId))
            {
                var setting = await db.SynchronizationSettings
                    .SingleAsync(x => x.VesselId == vesselId, cancellationToken);
                setting.AutomaticIntervalHours =
                    SynchronizationTokenService.NormalizeInterval(
                        incoming.AutomaticIntervalHours);
                setting.LastModifiedUtc = EnsureUtc(incoming.LastModifiedUtc);
                accepted++;
            }
        }

        await db.SaveSynchronizationChangesAsync(cancellationToken);
        return (accepted, conflicts, warnings);
    }

    private async Task<Equipment?> FindMatchingEquipmentAsync(
        EquipmentSynchronizationItem incoming,
        CancellationToken cancellationToken)
    {
        var candidates = await db.Equipment
            .IgnoreQueryFilters()
            .Where(x =>
                x.VesselId == incoming.VesselId &&
                x.Category == incoming.Category)
            .ToListAsync(cancellationToken);

        var incomingSerial = NormalizeKey(incoming.SerialNumber);
        var matches = !string.IsNullOrEmpty(incomingSerial)
            ? candidates.Where(x =>
                NormalizeKey(x.SerialNumber) == incomingSerial)
            : candidates.Where(x =>
                NormalizeKey(x.EquipmentName) ==
                    NormalizeKey(incoming.EquipmentName) &&
                NormalizeKey(x.Maker) == NormalizeKey(incoming.Maker) &&
                NormalizeKey(x.Model) == NormalizeKey(incoming.Model));
        var materialized = matches.Take(2).ToList();
        return materialized.Count == 1 ? materialized[0] : null;
    }

    private bool ShouldApply(
        bool recordExists,
        string sourceEdition,
        string authoritativeEdition,
        string entityName,
        Guid entityId,
        List<string> warnings,
        ref int conflicts)
    {
        if (!recordExists)
            return true;

        if (sourceEdition.Equals(
            authoritativeEdition,
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        conflicts++;
        warnings.Add(
            $"Skipped {entityName} {entityId}: " +
            $"{authoritativeEdition} edition owns this data type.");
        return false;
    }

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static string NormalizeKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
}
