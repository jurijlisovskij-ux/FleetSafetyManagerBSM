namespace FSM.Api.Models;

public sealed record EquipmentDocumentListItemDto(
    Guid Id,
    string Type,
    string FileName,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    string? DocumentNumber,
    string? Issuer,
    string? Remarks,
    bool IsCurrent,
    DateTime CreatedUtc,
    DateTime? ArchivedUtc);

public sealed record EquipmentDocumentMetadataDto(
    Guid Id,
    Guid EquipmentId,
    string Type,
    string FileName,
    string MimeType,
    long FileSize,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    string? DocumentNumber,
    string? Issuer,
    string? Remarks,
    bool IsCurrent,
    DateTime CreatedUtc,
    DateTime? ArchivedUtc);
