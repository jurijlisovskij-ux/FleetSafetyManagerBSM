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
}
