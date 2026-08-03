namespace FSM.Domain.Entities;

public sealed class InspectionReport : ISynchronizableEntity
{
    public Guid Id { get; set; }
    public Guid VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public required string ReportNumber { get; set; }
    public DateOnly InspectionDate { get; set; }
    public required string Inspector { get; set; }
    public required string PdfPath { get; set; }
    public required string PdfSha256 { get; set; }
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public ICollection<InspectionReportItem> Items { get; set; } = [];
}

public sealed class InspectionReportItem : ISynchronizableEntity
{
    public Guid Id { get; set; }
    public Guid InspectionReportId { get; set; }
    public InspectionReport? InspectionReport { get; set; }
    public Guid EquipmentId { get; set; }
    public Equipment? Equipment { get; set; }
    public required string Result { get; set; }
    public string? Remarks { get; set; }
    public string? CorrectiveAction { get; set; }
    public string? AttachmentPath { get; set; }
    public DateOnly? ClosureDate { get; set; }
    public string? ClosedBy { get; set; }
    public string? ClosureRemarks { get; set; }
    public string? ClosureAttachmentPath { get; set; }
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
