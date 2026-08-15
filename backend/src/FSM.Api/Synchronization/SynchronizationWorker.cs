using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Synchronization;

public sealed class SynchronizationWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IOptions<SynchronizationOptions> options,
    ILogger<SynchronizationWorker> logger) : BackgroundService
{
    private readonly HashSet<Guid> startupAttempts = [];

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var edition = configuration["Application:Edition"] ?? "Office";
        if (!edition.Equals("Ship", StringComparison.OrdinalIgnoreCase))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueSynchronizationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "The automatic synchronization scheduler failed.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(1, options.Value.SchedulerPollSeconds)),
                stoppingToken);
        }
    }

    private async Task RunDueSynchronizationsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> vesselIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FsmDbContext>();
            var configuredId = configuration["Application:ShipVesselId"];
            var query = db.Vessels.AsNoTracking();
            if (Guid.TryParse(configuredId, out var shipVesselId))
                query = query.Where(x => x.Id == shipVesselId);
            else
                query = query.Where(x => db.SynchronizationSettings.Any(
                    setting => setting.VesselId == x.Id &&
                        setting.Enabled &&
                        setting.RemoteBaseUrl != null &&
                        setting.ProtectedToken != null));
            vesselIds = await query
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var vesselId in vesselIds)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var tokens = scope.ServiceProvider
                .GetRequiredService<SynchronizationTokenService>();
            var setting = await tokens.GetOrCreateSettingAsync(
                vesselId,
                cancellationToken);
            if (!setting.Enabled)
                continue;
            var interval = SynchronizationTokenService.NormalizeInterval(
                setting.AutomaticIntervalHours);
            if (interval == 0)
                continue;

            var now = DateTime.UtcNow;
            if (setting.NextRetryUtc.HasValue && setting.NextRetryUtc > now)
                continue;

            var status = scope.ServiceProvider
                .GetRequiredService<SynchronizationStatusService>();
            var files = scope.ServiceProvider
                .GetRequiredService<SynchronizationFileService>();
            var db = scope.ServiceProvider.GetRequiredService<FsmDbContext>();
            var vessel = await db.Vessels.AsNoTracking().SingleAsync(
                x => x.Id == vesselId,
                cancellationToken);
            var pending = await status.CountPendingAsync(
                vesselId,
                setting.LastUploadedUtc,
                cancellationToken) + files.CountChangedFiles(
                    vessel,
                    setting.LastUploadedUtc);
            var scheduledAt = (setting.LastSynchronizationUtc ?? DateTime.MinValue)
                .AddHours(interval);
            var startupDue = startupAttempts.Add(vesselId);
            if (!startupDue && setting.LastSynchronizationUtc.HasValue &&
                pending == 0 && scheduledAt > now)
            {
                continue;
            }

            try
            {
                var client = scope.ServiceProvider
                    .GetRequiredService<SynchronizationClient>();
                await client.RunAsync(vesselId, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Automatic synchronization failed for vessel {VesselId}. " +
                    "Exponential retry is scheduled automatically.",
                    vesselId);
            }
        }
    }
}
