using FSM.Domain.Enums;

namespace FSM.Domain.Entities;

public sealed class EquipmentDocument
{
    public Guid Id { get; set; }
    public Guid EquipmentId { get; set; }
    public Equipment? Equipment { get; set; }
    public EquipmentDocumentType DocumentType { get; set; }
    public required string FileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string MimeType { get; set; }
    public long FileSize { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? DocumentNumber { get; set; }
    public string? Issuer { get; set; }
    public string? Remarks { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ArchivedUtc { get; set; }
}
