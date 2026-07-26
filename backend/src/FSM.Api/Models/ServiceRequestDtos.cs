using FSM.Domain.Entities;

namespace FSM.Api.Models;

public sealed record CreateManagedServiceRequest(
    Guid VesselId, DateOnly RequestDate, string ContactName, string ContactEmail,
    string? ContactPhone, string? PreferredPort, DateOnly? ExpectedArrivalDate,
    DateOnly? ExpectedDepartureDate, string? ServiceCompany, string? AttentionTo,
    string Subject, string? GeneralRemarks);
public sealed record UpdateManagedServiceRequest(
    DateOnly RequestDate, string ContactName, string ContactEmail,
    string? ContactPhone, string? PreferredPort, DateOnly? ExpectedArrivalDate,
    DateOnly? ExpectedDepartureDate, string? ServiceCompany, string? AttentionTo,
    string Subject, string? GeneralRemarks, string? InvoicingDetails);
public sealed record AddServiceRequestItems(
    ServiceRequestItemType ItemType, IReadOnlyList<Guid>? SourceIds,
    string? Title, string? Location, string? Description,
    string ServiceDescription, string? Remarks, bool AllowDuplicate = false);
public sealed record UpdateServiceRequestItem(
    string Title, string? Location, string? Description,
    string ServiceDescription, string? Remarks);
public sealed record ReorderServiceRequestItems(IReadOnlyList<Guid> ItemIds);
public sealed record ServiceRequestReason(string Reason);
public sealed record ServiceRequestAttachmentUpdate(string? Description);

public sealed record ServiceRequestListDto(
    Guid Id, string RequestNumber, Guid VesselId, string VesselName,
    DateOnly RequestDate, string Subject, string? PreferredPort,
    ServiceRequestStatus Status, int ItemCount, string RequestedBy,
    DateTime UpdatedUtc);
public sealed record ServiceRequestItemDto(
    Guid Id, int SequenceNumber, ServiceRequestItemType ItemType,
    Guid? EquipmentId, Guid? InspectionFindingId, string? InspectionReportNumber,
    int? InspectionFindingSequence, string Title, string? Category, string? Maker,
    string? Model, string? SerialNumber, string? Location,
    DateOnly? CurrentDueDate, string? Priority, string? Description,
    string ServiceDescription, string? Remarks);
public sealed record ServiceRequestAttachmentDto(
    Guid Id, string OriginalFileName, string MimeType, long FileSize,
    string? Description, DateTime CreatedUtc, string CreatedBy);
public sealed record ServiceRequestPdfDto(
    Guid Id, int VersionNumber, string FileName, long FileSize,
    DateTime GeneratedUtc, string GeneratedBy, bool IsCurrent);
public sealed record ManagedServiceRequestDto(
    Guid Id, string RequestNumber, Guid VesselId, string VesselName,
    string ImoNumber, DateOnly RequestDate, string RequestedBy,
    string ContactName, string ContactEmail, string? ContactPhone,
    string? PreferredPort, DateOnly? ExpectedArrivalDate,
    DateOnly? ExpectedDepartureDate, string? ServiceCompany, string? AttentionTo,
    string Subject, string? GeneralRemarks, string? InvoicingDetails,
    ServiceRequestStatus Status, DateTime CreatedUtc, DateTime UpdatedUtc,
    DateTime? SentUtc, DateTime? CompletedUtc, DateTime? CancelledUtc,
    string? CancellationReason, DateTime? ReopenedUtc, string? ReopenedBy,
    string? ReopenReason, IReadOnlyList<ServiceRequestItemDto> Items,
    IReadOnlyList<ServiceRequestAttachmentDto> Attachments);
