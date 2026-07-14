using System.Net;
using System.Text;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Services;

public sealed class DailyNotificationWorker(
    IServiceScopeFactory scopeFactory,
    EmailSender emailSender,
    IOptions<NotificationSettings> options,
    ILogger<DailyNotificationWorker> logger)
    : BackgroundService
{
    private readonly NotificationSettings settings =
        options.Value;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun(
                settings.HourUtc);

            await Task.Delay(delay, stoppingToken);

            if (!settings.Enabled ||
                !emailSender.IsConfigured)
            {
                logger.LogInformation(
                    "FSM email notifications are disabled or SMTP is not configured.");
                continue;
            }

            try
            {
                await SendNotificationsAsync(
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Daily FSM notification job failed.");
            }
        }
    }

    private async Task SendNotificationsAsync(
        CancellationToken cancellationToken)
    {
        using var scope =
            scopeFactory.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FsmDbContext>();

        var today =
            DateOnly.FromDateTime(DateTime.UtcNow);
        var soonLimit = today.AddDays(30);

        var users = await db.Users
            .AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => new
            {
                x.Id,
                x.FullName,
                x.Email,
                x.Role,
                VesselIds = x.VesselAssignments
                    .Select(a => a.VesselId)
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        foreach (var user in users)
        {
            var query = db.Equipment
                .AsNoTracking()
                .Include(x => x.Vessel)
                .Where(x =>
                    x.ExpiryDate != null &&
                    x.ExpiryDate <= soonLimit);

            if (user.Role != UserRole.OfficeAdmin)
            {
                query = query.Where(x =>
                    user.VesselIds.Contains(x.VesselId));
            }

            var items = await query
                .OrderBy(x => x.Vessel!.VesselName)
                .ThenBy(x => x.Category)
                .ThenBy(x => x.ExpiryDate)
                .ThenBy(x => x.EquipmentName)
                .ToListAsync(cancellationToken);

            if (items.Count == 0)
                continue;

            var html = BuildHtml(
                user.FullName,
                items,
                today);

            await emailSender.SendAsync(
                user.Email,
                "Fleet Safety Manager — Soon and Overdue Equipment",
                html,
                cancellationToken);
        }
    }

    private static string BuildHtml(
        string fullName,
        IReadOnlyList<FSM.Domain.Entities.Equipment> items,
        DateOnly today)
    {
        var builder = new StringBuilder();

        builder.Append("<html><body style=\"font-family:Segoe UI,Arial,sans-serif\">");
        builder.Append("<h2>Fleet Safety Manager</h2>");
        builder.Append("<p>Hello ");
        builder.Append(WebUtility.HtmlEncode(fullName));
        builder.Append(",</p>");
        builder.Append("<p>The following equipment requires attention.</p>");

        foreach (var vesselGroup in items
            .GroupBy(x => new
            {
                x.VesselId,
                Name = x.Vessel!.VesselName,
                x.Vessel.ImoNumber
            }))
        {
            builder.Append("<h3>");
            builder.Append(WebUtility.HtmlEncode(
                vesselGroup.Key.Name));
            builder.Append(" — IMO ");
            builder.Append(WebUtility.HtmlEncode(
                vesselGroup.Key.ImoNumber));
            builder.Append("</h3>");

            foreach (var categoryGroup in vesselGroup
                .GroupBy(x => x.Category))
            {
                builder.Append("<h4>");
                builder.Append(WebUtility.HtmlEncode(
                    FormatCategory(categoryGroup.Key)));
                builder.Append("</h4>");

                builder.Append(
                    "<table cellpadding=\"6\" cellspacing=\"0\" " +
                    "style=\"border-collapse:collapse;border:1px solid #d1d5db\">");
                builder.Append(
                    "<tr style=\"background:#f3f4f6\">" +
                    "<th align=\"left\">Equipment</th>" +
                    "<th align=\"left\">Maker / Model</th>" +
                    "<th align=\"left\">Expiry Date</th>" +
                    "<th align=\"left\">Status</th></tr>");

                foreach (var item in categoryGroup)
                {
                    var isOverdue =
                        item.ExpiryDate < today;

                    var status = isOverdue
                        ? "Overdue"
                        : "Soon";

                    var statusColor = isOverdue
                        ? "#b91c1c"
                        : "#92400e";

                    builder.Append("<tr>");
                    builder.Append("<td style=\"border:1px solid #d1d5db\">");
                    builder.Append(WebUtility.HtmlEncode(
                        item.EquipmentName));
                    builder.Append("</td>");

                    builder.Append("<td style=\"border:1px solid #d1d5db\">");
                    builder.Append(WebUtility.HtmlEncode(
                        string.Join(
                            " / ",
                            new[] { item.Maker, item.Model }
                                .Where(x =>
                                    !string.IsNullOrWhiteSpace(x)))));
                    builder.Append("</td>");

                    builder.Append("<td style=\"border:1px solid #d1d5db\">");
                    builder.Append(item.ExpiryDate?
                        .ToString("dd MMM yyyy"));
                    builder.Append("</td>");

                    builder.Append("<td style=\"border:1px solid #d1d5db;color:");
                    builder.Append(statusColor);
                    builder.Append(";font-weight:600\">");
                    builder.Append(status);
                    builder.Append("</td>");
                    builder.Append("</tr>");
                }

                builder.Append("</table>");
            }
        }

        builder.Append(
            "<p style=\"margin-top:20px;color:#6b7280\">" +
            "This is an automatic Fleet Safety Manager notification." +
            "</p></body></html>");

        return builder.ToString();
    }

    private static string FormatCategory(
        EquipmentCategory category) =>
        category == EquipmentCategory.RadioAndNavigation
            ? "Radio & Navigation"
            : category.ToString();

    private static TimeSpan GetDelayUntilNextRun(
        int configuredHour)
    {
        var hour = Math.Clamp(
            configuredHour,
            0,
            23);

        var now = DateTimeOffset.UtcNow;
        var next = new DateTimeOffset(
            now.Year,
            now.Month,
            now.Day,
            hour,
            0,
            0,
            TimeSpan.Zero);

        if (next <= now)
            next = next.AddDays(1);

        return next - now;
    }
}
