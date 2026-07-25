using FSM.Domain.Entities;

namespace FSM.Api.Models;

public sealed record InspectionReportCreateRequest(
    Guid VesselId, DateOnly InspectionDate, string InspectorName,
    InspectionType InspectionType, string? GeneralRemarks);
public sealed record InspectionReportUpdateRequest(
    DateOnly InspectionDate, string InspectorName,
    InspectionType InspectionType, string? GeneralRemarks);
public sealed record InspectionFindingRequest(
    InspectionDepartment Department, string EquipmentOrArea,
    string? ExactLocation, string Description, InspectionPriority Priority,
    InspectionFindingStatus Status, string? InspectorRemarks);
public sealed record ReorderFindingsRequest(IReadOnlyList<Guid> FindingIds);
public sealed record InspectionPhotoUpdateRequest(string? Caption);
public sealed record ReopenInspectionRequest(string Reason);

public sealed record InspectionPhotoDto(
    Guid Id, string OriginalFileName, string MimeType, long FileSize,
    string? Caption, DateTime CreatedUtc);
public sealed record InspectionFindingDto(
    Guid Id, int SequenceNumber, InspectionDepartment Department,
    string EquipmentOrArea, string? ExactLocation, string Description,
    InspectionPriority Priority, InspectionFindingStatus Status,
    string? InspectorRemarks, DateTime CreatedUtc, DateTime UpdatedUtc,
    IReadOnlyList<InspectionPhotoDto> Photos);
public sealed record InspectionReportListDto(
    Guid Id, string ReportNumber, Guid VesselId, string VesselName,
    DateOnly InspectionDate, string InspectorName, InspectionType InspectionType,
    InspectionReportState State, int FindingCount, int OpenFindingCount,
    DateTime CreatedUtc);
public sealed record InspectionReportDto(
    Guid Id, string ReportNumber, Guid VesselId, string VesselName,
    string ImoNumber, DateOnly InspectionDate, string InspectorName,
    InspectionType InspectionType, string? GeneralRemarks,
    InspectionReportState State, DateTime CreatedUtc, DateTime UpdatedUtc,
    DateTime? ReopenedUtc, string? ReopenedBy, string? ReopeningReason,
    IReadOnlyList<InspectionFindingDto> Findings);
public sealed record InspectionPdfDto(
    Guid Id, int VersionNumber, string FileName, long FileSize,
    DateTime GeneratedUtc, string GeneratedBy, bool IsCurrent);
