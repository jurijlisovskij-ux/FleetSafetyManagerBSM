using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Synchronization;

public sealed record DiscoveredOffice(
    string BaseUrl,
    OfficeActivationCatalog Catalog);

public sealed class OfficeDiscoveryService(
    FsmDbContext db,
    IHttpClientFactory httpClientFactory,
    IOptions<SynchronizationOptions> options)
{
    internal const string DiscoveryRequest = "FSM_DISCOVER_RC20_026";
    private readonly SynchronizationOptions options = options.Value;

    public async Task<IReadOnlyList<DiscoveredOffice>> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidate(candidates, options.OfficeBaseUrl);
        AddCandidate(candidates, options.AdvertisedBaseUrl);

        var persisted = await db.SynchronizationSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.RemoteBaseUrl != null)
            .Select(x => x.RemoteBaseUrl!)
            .ToListAsync(cancellationToken);
        foreach (var endpoint in persisted)
            AddCandidate(candidates, endpoint);

        foreach (var endpoint in await DiscoverUdpEndpointsAsync(cancellationToken))
            AddCandidate(candidates, endpoint);

        var discovered = new List<DiscoveredOffice>();
        var client = httpClientFactory.CreateClient("Synchronization");
        foreach (var endpoint in candidates)
        {
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                probe.CancelAfter(Math.Max(
                    500,
                    options.DiscoveryTimeoutMilliseconds));
                var catalog = await client.GetFromJsonAsync<OfficeActivationCatalog>(
                    $"{endpoint}/api/synchronization/activation/catalog",
                    probe.Token);
                if (catalog is not null && string.Equals(
                    catalog.ApplicationVersion,
                    options.ApplicationVersion,
                    StringComparison.Ordinal))
                {
                    discovered.Add(new DiscoveredOffice(endpoint, catalog));
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or OperationCanceledException or JsonException)
            {
                // Discovery probes are expected to encounter unavailable hosts.
            }
        }
        return discovered;
    }

    private async Task<IReadOnlyList<string>> DiscoverUdpEndpointsAsync(
        CancellationToken cancellationToken)
    {
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true
            };
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var request = Encoding.ASCII.GetBytes(DiscoveryRequest);
            await udp.SendAsync(
                request,
                new IPEndPoint(IPAddress.Broadcast, options.DiscoveryPort),
                cancellationToken);
            await udp.SendAsync(
                request,
                new IPEndPoint(IPAddress.Loopback, options.DiscoveryPort),
                cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(Math.Max(100, options.DiscoveryTimeoutMilliseconds));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult response;
                try
                {
                    response = await udp.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                try
                {
                    var payload = JsonSerializer.Deserialize<DiscoveryResponse>(
                        response.Buffer);
                    AddCandidate(endpoints, payload?.BaseUrl);
                }
                catch (JsonException)
                {
                    // Ignore unrelated UDP traffic on the discovery port.
                }
            }
        }
        catch (Exception exception) when (
            exception is SocketException or InvalidOperationException)
        {
            // A blocked broadcast socket falls back to persisted/internal endpoints.
        }
        return endpoints.ToList();
    }

    private static void AddCandidate(ISet<string> candidates, string? value)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            candidates.Add(uri.ToString().TrimEnd('/'));
        }
    }

    internal sealed record DiscoveryResponse(
        string BaseUrl,
        string ApplicationVersion);
}
