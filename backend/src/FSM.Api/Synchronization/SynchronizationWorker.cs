using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Synchronization;

public sealed class SynchronizationWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SynchronizationWorker> logger) : BackgroundService
{
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

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
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
            var interval = SynchronizationTokenService.NormalizeInterval(
                setting.AutomaticIntervalHours);
            if (interval == 0)
                continue;

            var lastAttempt = setting.LastAttemptUtc ?? DateTime.MinValue;
            if (lastAttempt.AddHours(interval) > DateTime.UtcNow)
                continue;

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
                    "The next attempt will use the configured interval.",
                    vesselId);
            }
        }
    }
}
