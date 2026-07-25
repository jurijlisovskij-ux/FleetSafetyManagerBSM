namespace FSM.Domain.Entities;

public enum InspectionType { Deck, Engine, DeckAndEngine }
public enum InspectionReportState { Draft, Finalized }
public enum InspectionDepartment { Deck, Engine }
public enum InspectionPriority { Critical, Urgent, Normal }
public enum InspectionFindingStatus { Open, Closed }

public sealed class InspectionReport
{
    public Guid Id { get; set; }
    public Guid VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public required string ReportNumber { get; set; }
    public DateOnly InspectionDate { get; set; }
    public required string InspectorName { get; set; }
    public InspectionType InspectionType { get; set; }
    public string? GeneralRemarks { get; set; }
    public InspectionReportState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? ReopenedUtc { get; set; }
    public string? ReopenedBy { get; set; }
    public string? ReopeningReason { get; set; }
    public ICollection<InspectionFinding> Findings { get; set; } = [];
    public ICollection<InspectionReportPdf> PdfVersions { get; set; } = [];
}

public sealed class InspectionFinding
{
    public Guid Id { get; set; }
    public Guid InspectionReportId { get; set; }
    public InspectionReport? InspectionReport { get; set; }
    public int SequenceNumber { get; set; }
    public InspectionDepartment Department { get; set; }
    public required string EquipmentOrArea { get; set; }
    public string? ExactLocation { get; set; }
    public required string Description { get; set; }
    public InspectionPriority Priority { get; set; }
    public InspectionFindingStatus Status { get; set; }
    public string? InspectorRemarks { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public ICollection<InspectionPhoto> Photos { get; set; } = [];
}

public sealed class InspectionPhoto
{
    public Guid Id { get; set; }
    public Guid FindingId { get; set; }
    public InspectionFinding? Finding { get; set; }
    public required string OriginalFileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string MimeType { get; set; }
    public long FileSize { get; set; }
    public string? Caption { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class InspectionReportPdf
{
    public Guid Id { get; set; }
    public Guid InspectionReportId { get; set; }
    public InspectionReport? InspectionReport { get; set; }
    public int VersionNumber { get; set; }
    public required string FileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string MimeType { get; set; }
    public long FileSize { get; set; }
    public DateTime GeneratedUtc { get; set; }
    public required string GeneratedBy { get; set; }
    public bool IsCurrent { get; set; }
}
