using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSM.Api.Synchronization;

public sealed class SynchronizationClient(
    FsmDbContext db,
    SynchronizationTokenService tokens,
    SynchronizationExchangeService exchange,
    SynchronizationFileService files,
    IHttpClientFactory httpClientFactory,
    IOptions<SynchronizationOptions> options,
    IConfiguration configuration)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim>
        VesselLocks = new();
    private readonly SynchronizationOptions options = options.Value;
    private readonly string edition =
        configuration["Application:Edition"] ?? "Office";

    public async Task<SynchronizationRunResult> RunAsync(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var gate = VesselLocks.GetOrAdd(
            vesselId,
            _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException(
                "Synchronization is already running for this vessel.");
        }

        var stopwatch = Stopwatch.StartNew();
        var uploaded = 0;
        var downloaded = 0;
        SynchronizationSetting? setting = null;

        try
        {
            setting = await tokens.GetOrCreateSettingAsync(
                vesselId,
                cancellationToken);
            var vessel = await db.Vessels.SingleAsync(
                x => x.Id == vesselId,
                cancellationToken);
            if (!setting.Enabled)
                throw new InvalidOperationException(
                    "Synchronization is disabled for this vessel.");
            var remoteBaseUrl = tokens.GetRemoteBaseUrl(setting, edition);
            var token = tokens.GetOutboundToken(setting);
            var client = httpClientFactory.CreateClient("Synchronization");

            await SetStageAsync(
                setting,
                "Checking connection...",
                cancellationToken);
            var handshake = await GetHandshakeAsync(
                client,
                remoteBaseUrl,
                token,
                vesselId,
                vessel.ImoNumber,
                cancellationToken);
            ValidateVersion(handshake.ApplicationVersion);
            if (!string.Equals(handshake.VesselImo, vessel.ImoNumber,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The remote vessel IMO does not match the configured local vessel.");

            var localSnapshotUtc = DateTime.UtcNow;
            var outgoing = await exchange.BuildPayloadAsync(
                vesselId,
                setting.LastUploadedUtc,
                localSnapshotUtc,
                cancellationToken);

            await SetStageAsync(
                setting,
                "Uploading changes...",
                cancellationToken);
            foreach (var file in outgoing.Files)
            {
                await UploadFileAsync(
                    client,
                    remoteBaseUrl,
                    token,
                    vessel,
                    file,
                    cancellationToken);
                uploaded++;
            }

            var request = new SynchronizationExchangeRequest(
                options.ApplicationVersion,
                edition,
                vesselId,
                setting.RemoteCursorUtc,
                outgoing);
            using var exchangeRequest = CreateRequest(
                HttpMethod.Post,
                $"{remoteBaseUrl}/api/synchronization/exchange",
                token);
            exchangeRequest.Content = JsonContent.Create(request);
            using var exchangeResponse = await client.SendAsync(
                exchangeRequest,
                cancellationToken);
            await EnsureSuccessAsync(exchangeResponse, cancellationToken);
            var response = await exchangeResponse.Content
                .ReadFromJsonAsync<SynchronizationExchangeResponse>(
                    cancellationToken: cancellationToken)
                ?? throw new InvalidDataException(
                    "The synchronization response was empty.");
            ValidateVersion(response.ApplicationVersion);
            if (response.VesselId != vesselId)
                throw new InvalidDataException(
                    "The synchronization response vessel does not match.");

            uploaded += outgoing.RecordCount;
            await SetStageAsync(
                setting,
                "Downloading updates...",
                cancellationToken);
            var applied = await exchange.ApplyAsync(
                response.Changes,
                vesselId,
                setting.LastUploadedUtc,
                response.SourceEdition,
                cancellationToken);
            downloaded += applied.Accepted;

            foreach (var file in response.Changes.Files)
            {
                if (await files.IsCurrentAsync(
                    vessel,
                    file,
                    cancellationToken))
                {
                    continue;
                }
                await DownloadFileAsync(
                    client,
                    remoteBaseUrl,
                    token,
                    vessel,
                    file,
                    cancellationToken);
                downloaded++;
            }

            var conflicts = response.ConflictItems + applied.Conflicts;
            var status = conflicts == 0 ? "Success" : "Warning";
            var message = conflicts == 0
                ? null
                : $"{conflicts} newer local item(s) were retained.";
            setting.LastUploadedUtc = localSnapshotUtc;
            setting.RemoteCursorUtc = response.SnapshotUtc;
            setting.LastSynchronizationUtc = DateTime.UtcNow;
            setting.LastStatus = status;
            setting.LastError = message;
            setting.CurrentStage = "Synchronization completed.";
            await db.SaveChangesAsync(cancellationToken);
            await RecordLogAsync(
                vesselId,
                $"{edition} ↔ {handshake.Edition}",
                stopwatch.ElapsedMilliseconds,
                uploaded,
                downloaded,
                status,
                message,
                remoteBaseUrl,
                null,
                setting.CurrentStage,
                conflicts,
                cancellationToken);

            return new SynchronizationRunResult(
                vesselId,
                setting.LastSynchronizationUtc.Value,
                status,
                uploaded,
                downloaded,
                conflicts,
                message);
        }
        catch (Exception exception)
        {
            if (setting is not null)
            {
                setting.LastAttemptUtc = DateTime.UtcNow;
                setting.LastStatus = "Failed";
                setting.LastError = exception.Message;
                setting.CurrentStage = "Failed";
                await db.SaveChangesAsync(CancellationToken.None);
                await RecordLogAsync(
                    vesselId,
                    $"{edition} ↔ Remote",
                    stopwatch.ElapsedMilliseconds,
                    uploaded,
                    downloaded,
                    "Failed",
                    exception.Message,
                    setting.RemoteBaseUrl,
                    (exception as HttpRequestException)?.StatusCode,
                    setting.CurrentStage,
                    0,
                    CancellationToken.None);
            }
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SynchronizationHandshakeResponse> GetHandshakeAsync(
        HttpClient client,
        string remoteBaseUrl,
        string token,
        Guid vesselId,
        string vesselImo,
        CancellationToken cancellationToken)
    {
        var url =
            $"{remoteBaseUrl}/api/synchronization/handshake" +
            $"?vesselId={vesselId:D}" +
            $"&vesselImo={Uri.EscapeDataString(vesselImo)}" +
            $"&version={Uri.EscapeDataString(options.ApplicationVersion)}";
        using var request = CreateRequest(HttpMethod.Get, url, token);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content
            .ReadFromJsonAsync<SynchronizationHandshakeResponse>(
                cancellationToken: cancellationToken)
            ?? throw new InvalidDataException(
                "The synchronization handshake was empty.");
    }

    public async Task<SynchronizationHandshakeResponse> TestConnectionAsync(
        Guid vesselId,
        CancellationToken cancellationToken)
    {
        var setting = await tokens.GetOrCreateSettingAsync(vesselId, cancellationToken);
        var vessel = await db.Vessels.AsNoTracking().SingleAsync(
            x => x.Id == vesselId, cancellationToken);
        var remoteBaseUrl = tokens.GetRemoteBaseUrl(setting, edition);
        var token = tokens.GetOutboundToken(setting);
        var response = await GetHandshakeAsync(
            httpClientFactory.CreateClient("Synchronization"),
            remoteBaseUrl,
            token,
            vesselId,
            vessel.ImoNumber,
            cancellationToken);
        ValidateVersion(response.ApplicationVersion);
        if (!string.Equals(response.VesselImo, vessel.ImoNumber,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The remote vessel IMO does not match the configured local vessel.");
        return response;
    }

    private async Task UploadFileAsync(
        HttpClient client,
        string remoteBaseUrl,
        string token,
        Vessel vessel,
        SynchronizationFileItem file,
        CancellationToken cancellationToken)
    {
        using var prepareRequest = CreateRequest(
            HttpMethod.Post,
            $"{remoteBaseUrl}/api/synchronization/file-transfers/prepare",
            token);
        prepareRequest.Content = JsonContent.Create(
            new PrepareSynchronizationFileRequest(
                options.ApplicationVersion,
                vessel.Id,
                file));
        using var prepareResponse = await client.SendAsync(
            prepareRequest,
            cancellationToken);
        await EnsureSuccessAsync(prepareResponse, cancellationToken);
        var prepared = await prepareResponse.Content
            .ReadFromJsonAsync<PrepareSynchronizationFileResponse>(
                cancellationToken: cancellationToken)
            ?? throw new InvalidDataException(
                "The file transfer response was empty.");
        if (prepared.Completed)
            return;
        if (prepared.TransferId is null)
            throw new InvalidDataException("The file transfer ID is missing.");

        var sourcePath = files.GetAbsolutePath(vessel, file.RelativePath);
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            options.ChunkSizeBytes,
            useAsync: true);
        var offset = prepared.NextOffset;
        input.Position = offset;
        var buffer = new byte[options.ChunkSizeBytes];
        while (offset < file.Size)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                throw new EndOfStreamException(
                    "The synchronization file ended unexpectedly.");
            using var chunkRequest = CreateRequest(
                HttpMethod.Put,
                $"{remoteBaseUrl}/api/synchronization/file-transfers/" +
                $"{prepared.TransferId:D}/chunks?vesselId={vessel.Id:D}" +
                $"&offset={offset}&version=" +
                Uri.EscapeDataString(options.ApplicationVersion),
                token);
            chunkRequest.Content = new ByteArrayContent(buffer, 0, read);
            chunkRequest.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "application/octet-stream");
            using var chunkResponse = await client.SendAsync(
                chunkRequest,
                cancellationToken);
            await EnsureSuccessAsync(chunkResponse, cancellationToken);
            offset += read;
        }

        using var completeRequest = CreateRequest(
            HttpMethod.Post,
            $"{remoteBaseUrl}/api/synchronization/file-transfers/" +
            $"{prepared.TransferId:D}/complete",
            token);
        completeRequest.Content = JsonContent.Create(
            new CompleteSynchronizationFileRequest(
                options.ApplicationVersion,
                vessel.Id));
        using var completeResponse = await client.SendAsync(
            completeRequest,
            cancellationToken);
        await EnsureSuccessAsync(completeResponse, cancellationToken);
    }

    private async Task DownloadFileAsync(
        HttpClient client,
        string remoteBaseUrl,
        string token,
        Vessel vessel,
        SynchronizationFileItem file,
        CancellationToken cancellationToken)
    {
        var offset = files.GetDownloadOffset(vessel.Id, file);
        while (offset < file.Size)
        {
            var url =
                $"{remoteBaseUrl}/api/synchronization/files/chunk" +
                $"?vesselId={vessel.Id:D}" +
                $"&path={Uri.EscapeDataString(file.RelativePath)}" +
                $"&size={file.Size}" +
                $"&checksum={Uri.EscapeDataString(file.Checksum)}" +
                $"&lastModifiedUtc={Uri.EscapeDataString(file.LastModifiedUtc.ToString("O"))}" +
                $"&offset={offset}" +
                $"&version={Uri.EscapeDataString(options.ApplicationVersion)}";
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var content = await response.Content
                .ReadAsByteArrayAsync(cancellationToken);
            if (content.Length == 0)
                throw new EndOfStreamException(
                    "The remote synchronization file ended unexpectedly.");
            await files.AppendDownloadAsync(
                vessel.Id,
                file,
                offset,
                content,
                cancellationToken);
            offset += content.Length;
        }
        await files.CompleteDownloadAsync(
            vessel,
            file,
            cancellationToken);
    }

    private async Task SetStageAsync(
        SynchronizationSetting setting,
        string stage,
        CancellationToken cancellationToken)
    {
        setting.LastAttemptUtc = DateTime.UtcNow;
        setting.CurrentStage = stage;
        setting.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordLogAsync(
        Guid vesselId,
        string direction,
        long durationMilliseconds,
        int uploaded,
        int downloaded,
        string result,
        string? error,
        string? endpoint,
        System.Net.HttpStatusCode? httpStatus,
        string? phase,
        int rejected,
        CancellationToken cancellationToken)
    {
        db.SynchronizationLogs.Add(new SynchronizationLog
        {
            Id = Guid.NewGuid(),
            VesselId = vesselId,
            DateUtc = DateTime.UtcNow,
            Direction = direction,
            DurationMilliseconds = durationMilliseconds,
            UploadedItems = uploaded,
            DownloadedItems = downloaded,
            Result = result,
            ErrorMessage = error,
            EndpointHost = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                ? uri.Host
                : null,
            HttpStatus = httpStatus.HasValue ? (int)httpStatus.Value : null,
            Phase = phase,
            RejectedItems = rejected
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation(
            SynchronizationTokenService.HeaderName,
            token);
        return request;
    }

    private void ValidateVersion(string remoteVersion)
    {
        if (!string.Equals(
            remoteVersion,
            options.ApplicationVersion,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Application version mismatch. Local: {options.ApplicationVersion}; " +
                $"remote: {remoteVersion}.");
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var message = await response.Content
            .ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(message)
                ? $"Synchronization request failed ({(int)response.StatusCode})."
                : message,
            null,
            response.StatusCode);
    }
}
