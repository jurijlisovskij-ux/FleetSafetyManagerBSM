using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FSM.Api.Synchronization;

public sealed class SynchronizationDiscoveryWorker(
    IConfiguration configuration,
    IOptions<SynchronizationOptions> options,
    ILogger<SynchronizationDiscoveryWorker> logger) : BackgroundService
{
    private readonly SynchronizationOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(
            configuration["Application:Edition"],
            "Office",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var udp = new UdpClient(new IPEndPoint(
                IPAddress.Any,
                options.DiscoveryPort));
            while (!stoppingToken.IsCancellationRequested)
            {
                var request = await udp.ReceiveAsync(stoppingToken);
                if (!string.Equals(
                    Encoding.ASCII.GetString(request.Buffer),
                    OfficeDiscoveryService.DiscoveryRequest,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                var baseUrl = GetAdvertisedBaseUrl(request.RemoteEndPoint);
                var response = JsonSerializer.SerializeToUtf8Bytes(
                    new OfficeDiscoveryService.DiscoveryResponse(
                        baseUrl,
                        options.ApplicationVersion));
                await udp.SendAsync(response, request.RemoteEndPoint, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception)
        {
            logger.LogWarning(
                exception,
                "Automatic Office discovery is unavailable; persisted endpoints remain usable.");
        }
    }

    private string GetAdvertisedBaseUrl(IPEndPoint remoteEndpoint)
    {
        if (Uri.TryCreate(options.AdvertisedBaseUrl, UriKind.Absolute, out var configured))
            return configured.ToString().TrimEnd('/');

        using var route = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        route.Connect(remoteEndpoint);
        var localAddress = ((IPEndPoint)route.LocalEndPoint!).Address;
        return $"http://{localAddress}:{options.DiscoveryHttpPort}";
    }
}
