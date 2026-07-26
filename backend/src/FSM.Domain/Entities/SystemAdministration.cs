namespace FSM.Domain.Entities;

public sealed class SystemSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

public sealed class SystemAuditEntry
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string? ObjectId { get; set; }
    public string? Details { get; set; }
}

public sealed class SystemBackup
{
    public Guid Id { get; set; }
    public string BackupType { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "";
}
