namespace FSM.Domain.Entities;

public sealed class SynchronizationSetting
{
    public Guid VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public Guid SynchronizationIdentity { get; set; }
    public string? RemoteBaseUrl { get; set; }
    public string? TokenHash { get; set; }
    public string? ProtectedToken { get; set; }
    public string? ActivationCodeHash { get; set; }
    public DateTime? ActivationCodeExpiresUtc { get; set; }
    public DateTime? ActivationCodeUsedUtc { get; set; }
    public bool Enabled { get; set; }
    public bool AllowInsecureHttp { get; set; }
    public int AutomaticIntervalHours { get; set; } = 24;
    public DateTime LastUploadedUtc { get; set; } = DateTime.UnixEpoch;
    public DateTime RemoteCursorUtc { get; set; } = DateTime.UnixEpoch;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSynchronizationUtc { get; set; }
    public string LastStatus { get; set; } = "Warning";
    public string CurrentStage { get; set; } = "Idle";
    public string? LastError { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryUtc { get; set; }
}

public sealed class SynchronizationLog
{
    public Guid Id { get; set; }
    public Guid VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public DateTime DateUtc { get; set; }
    public string Direction { get; set; } = string.Empty;
    public long DurationMilliseconds { get; set; }
    public int UploadedItems { get; set; }
    public int DownloadedItems { get; set; }
    public string Result { get; set; } = "Success";
    public string? ErrorMessage { get; set; }
    public string? EndpointHost { get; set; }
    public int? HttpStatus { get; set; }
    public string? Phase { get; set; }
    public int RejectedItems { get; set; }
}

public sealed class SynchronizationTransfer
{
    public Guid Id { get; set; }
    public Guid VesselId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public string TemporaryPath { get; set; } = string.Empty;
    public DateTime FileLastModifiedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
