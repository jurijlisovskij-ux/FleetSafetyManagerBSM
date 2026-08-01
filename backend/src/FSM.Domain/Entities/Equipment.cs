using FSM.Domain.Enums;

namespace FSM.Domain.Entities;

public sealed class Equipment : ISynchronizableEntity
{
    public Guid Id { get; set; }
    public Guid VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public EquipmentCategory Category { get; set; }
    public required string EquipmentName { get; set; }
    public string? Maker { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public int Quantity { get; set; } = 1;
    public InspectionBy InspectionBy { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? Remarks { get; set; }
    public string? CurrentReportPath { get; set; }
    public string? CurrentReportFileName { get; set; }
    public string? CurrentCertificatePath { get; set; }
    public string? CurrentCertificateFileName { get; set; }
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }

    public EquipmentStatus CalculateStatus(DateOnly today, int reminderDays)
    {
        if (ExpiryDate is null) return EquipmentStatus.OK;
        if (ExpiryDate.Value < today) return EquipmentStatus.Overdue;
        return ExpiryDate.Value <= today.AddDays(reminderDays)
            ? EquipmentStatus.Soon
            : EquipmentStatus.OK;
    }
}
