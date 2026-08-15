namespace FSM.Domain.Entities;

public sealed class MedicineItem : ISynchronizableEntity
{
    public Guid Id { get; set; }
    public Guid VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public required string ItemName { get; set; }
    public string? FormStrength { get; set; }
    public decimal RequiredQuantity { get; set; }
    public decimal ActualQuantity { get; set; }
    public required string Unit { get; set; }
    public string? BatchNumber { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? StorageLocation { get; set; }
    public string? MedicalCategory { get; set; }
    public bool IsDangerousGoods { get; set; }
    public bool ReplacementRequired { get; set; }
    public string? Remarks { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsStandardTemplateItem { get; set; }
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}

public sealed class VesselMedicineConfiguration : ISynchronizableEntity
{
    public Guid VesselId { get; set; }
    public bool CategoryAEnabled { get; set; }
    public bool CategoryBEnabled { get; set; }
    public bool DangerousGoodsEnabled { get; set; }
    public int WarningDays { get; set; } = 60;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
