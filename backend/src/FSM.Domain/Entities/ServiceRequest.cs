namespace FSM.Domain.Entities;

public enum ServiceRequestStatus { Draft, Ready, Sent, Completed, Cancelled }
public enum ServiceRequestItemType { Equipment, InspectionFinding, Manual }

public sealed class ServiceRequest
{
    public Guid Id { get; set; }
    public required string RequestNumber { get; set; }
    public Guid VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateOnly RequestDate { get; set; }
    public required string RequestedBy { get; set; }
    public required string ContactName { get; set; }
    public required string ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? PreferredPort { get; set; }
    public DateOnly? ExpectedArrivalDate { get; set; }
    public DateOnly? ExpectedDepartureDate { get; set; }
    public string? ServiceCompany { get; set; }
    public string? AttentionTo { get; set; }
    public required string Subject { get; set; }
    public string? GeneralRemarks { get; set; }
    public string? InvoicingDetails { get; set; }
    public ServiceRequestStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime? CancelledUtc { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime? ReopenedUtc { get; set; }
    public string? ReopenedBy { get; set; }
    public string? ReopenReason { get; set; }
    public ICollection<ServiceRequestItem> Items { get; set; } = [];
    public ICollection<ServiceRequestAttachment> Attachments { get; set; } = [];
    public ICollection<ServiceRequestPdf> PdfVersions { get; set; } = [];
}

public sealed class ServiceRequestItem
{
    public Guid Id { get; set; }
    public Guid ServiceRequestId { get; set; }
    public ServiceRequest? ServiceRequest { get; set; }
    public int SequenceNumber { get; set; }
    public ServiceRequestItemType ItemType { get; set; }
    public Guid? EquipmentId { get; set; }
    public Equipment? Equipment { get; set; }
    public Guid? InspectionFindingId { get; set; }
    public InspectionFinding? InspectionFinding { get; set; }
    public string? InspectionReportNumber { get; set; }
    public int? InspectionFindingSequence { get; set; }
    public required string Title { get; set; }
    public string? Category { get; set; }
    public string? Maker { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Location { get; set; }
    public DateOnly? CurrentDueDate { get; set; }
    public string? Priority { get; set; }
    public string? Description { get; set; }
    public required string ServiceDescription { get; set; }
    public string? Remarks { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ServiceRequestAttachment
{
    public Guid Id { get; set; }
    public Guid ServiceRequestId { get; set; }
    public ServiceRequest? ServiceRequest { get; set; }
    public required string OriginalFileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string MimeType { get; set; }
    public long FileSize { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedUtc { get; set; }
    public required string CreatedBy { get; set; }
}

public sealed class ServiceRequestPdf
{
    public Guid Id { get; set; }
    public Guid ServiceRequestId { get; set; }
    public ServiceRequest? ServiceRequest { get; set; }
    public int VersionNumber { get; set; }
    public required string FileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string MimeType { get; set; }
    public long FileSize { get; set; }
    public DateTime GeneratedUtc { get; set; }
    public required string GeneratedBy { get; set; }
    public bool IsCurrent { get; set; }
}
