namespace FSM.Api.Synchronization;

public sealed class SynchronizationOptions
{
    public const string SectionName = "Synchronization";
    public const string CurrentApplicationVersion = "0.20.0-RC20";

    public string ApplicationVersion { get; set; } =
        CurrentApplicationVersion;
    public string OfficeBaseUrl { get; set; } = string.Empty;
    public string VesselToken { get; set; } = string.Empty;
    public int AutomaticIntervalHours { get; set; } = 24;
    public int ChunkSizeBytes { get; set; } = 1024 * 1024;
    public int RequestTimeoutMinutes { get; set; } = 10;
    public bool AllowInsecureHttp { get; set; }
    public long MaximumFileSizeBytes { get; set; } = 100L * 1024L * 1024L;
    public int SchedulerPollSeconds { get; set; } = 60;
    public int MaximumRetryMinutes { get; set; } = 60;
    public int InitialRetrySeconds { get; set; } = 30;
    public int DiscoveryPort { get; set; } = 50820;
    public int DiscoveryHttpPort { get; set; } = 8080;
    public int DiscoveryTimeoutMilliseconds { get; set; } = 1500;
    public string AdvertisedBaseUrl { get; set; } = string.Empty;
}
