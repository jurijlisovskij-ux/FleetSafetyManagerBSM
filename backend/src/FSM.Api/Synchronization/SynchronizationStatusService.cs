using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Synchronization;

public sealed class SynchronizationStatusService(
    FsmDbContext db,
    SynchronizationTokenService tokens,
    SynchronizationFileService files,
    IOptions<SynchronizationOptions> options,
    IConfiguration configuration)
{
    private readonly SynchronizationOptions options = options.Value;
    private readonly string edition =
        configuration["Application:Edition"] ?? "Office";

    public async Task<IReadOnlyList<SynchronizationStatusItem>> GetAsync(
        IReadOnlyList<Vessel> vessels,
        CancellationToken cancellationToken)
    {
        var result = new List<SynchronizationStatusItem>(vessels.Count);

        foreach (var vessel in vessels)
        {
            var setting = await tokens.GetOrCreateSettingAsync(
                vessel.Id,
                cancellationToken);
            var pending = await CountPendingAsync(
                vessel.Id,
                setting.LastUploadedUtc,
                cancellationToken);
            pending += files.CountChangedFiles(
                vessel,
                setting.LastUploadedUtc);
            var configured = setting.Enabled && edition.Equals(
                "Ship",
                StringComparison.OrdinalIgnoreCase)
                ? (!string.IsNullOrWhiteSpace(setting.RemoteBaseUrl) ||
                   !string.IsNullOrWhiteSpace(options.OfficeBaseUrl)) &&
                  (!string.IsNullOrWhiteSpace(setting.ProtectedToken) ||
                   !string.IsNullOrWhiteSpace(options.VesselToken))
                : setting.Enabled &&
                  !string.IsNullOrWhiteSpace(setting.TokenHash);
            DateTime? nextAttempt = null;
            if (edition.Equals("Ship", StringComparison.OrdinalIgnoreCase) &&
                setting.Enabled && setting.AutomaticIntervalHours > 0)
            {
                nextAttempt = setting.NextRetryUtc ??
                    (setting.LastSynchronizationUtc ?? DateTime.UtcNow)
                    .AddHours(setting.AutomaticIntervalHours);
            }

            var connectionState = setting.CurrentStage switch
            {
                "WaitingForInternet" => "WaitingForInternet",
                "Checking connection..." or
                "Uploading changes..." or
                "Downloading updates..." or
                "Synchronizing" => "Synchronizing",
                "Failed" => "Failed",
                _ when setting.LastSynchronizationUtc.HasValue => "Connected",
                _ => "WaitingForInternet"
            };

            result.Add(new SynchronizationStatusItem(
                vessel.Id,
                vessel.VesselName,
                vessel.ImoNumber,
                setting.LastSynchronizationUtc,
                setting.LastStatus,
                pending,
                setting.AutomaticIntervalHours,
                setting.CurrentStage,
                configured,
                setting.RemoteBaseUrl,
                setting.LastError,
                setting.Enabled,
                setting.AllowInsecureHttp,
                nextAttempt,
                connectionState,
                setting.RetryCount));
        }

        return result;
    }

    public async Task<int> CountPendingAsync(
        Guid vesselId,
        DateTime sinceUtc,
        CancellationToken cancellationToken)
    {
        var vesselChanges = await db.Vessels
            .IgnoreQueryFilters()
            .CountAsync(
                x => x.Id == vesselId &&
                     x.LastModifiedUtc > sinceUtc,
                cancellationToken);
        var equipmentChanges = await db.Equipment
            .IgnoreQueryFilters()
            .CountAsync(
                x => x.VesselId == vesselId &&
                     x.LastModifiedUtc > sinceUtc,
                cancellationToken);
        var assignmentChanges = await db.UserVessels
            .IgnoreQueryFilters()
            .CountAsync(
                x => x.VesselId == vesselId &&
                     x.LastModifiedUtc > sinceUtc,
                cancellationToken);
        var userIds = await db.UserVessels
            .IgnoreQueryFilters()
            .Where(x => x.VesselId == vesselId)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var userChanges = await db.Users
            .IgnoreQueryFilters()
            .CountAsync(
                x => userIds.Contains(x.Id) &&
                     x.LastModifiedUtc > sinceUtc,
                cancellationToken);

        var medicineChanges = await db.MedicineItems
            .IgnoreQueryFilters()
            .CountAsync(x => x.VesselId == vesselId &&
                x.LastModifiedUtc > sinceUtc, cancellationToken);
        var medicineConfigurationChanges = await db.VesselMedicineConfigurations
            .IgnoreQueryFilters()
            .CountAsync(x => x.VesselId == vesselId &&
                x.LastModifiedUtc > sinceUtc, cancellationToken);
        var inspectionChanges = await db.InspectionReports
            .IgnoreQueryFilters()
            .CountAsync(x => x.VesselId == vesselId &&
                x.LastModifiedUtc > sinceUtc, cancellationToken);
        var reportIds = await db.InspectionReports
            .IgnoreQueryFilters()
            .Where(x => x.VesselId == vesselId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var inspectionItemChanges = await db.InspectionReportItems
            .IgnoreQueryFilters()
            .CountAsync(x => reportIds.Contains(x.InspectionReportId) &&
                x.LastModifiedUtc > sinceUtc, cancellationToken);

        return vesselChanges + equipmentChanges + assignmentChanges +
            userChanges + medicineChanges + medicineConfigurationChanges +
            inspectionChanges + inspectionItemChanges;
    }
}
