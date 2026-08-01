namespace FSM.Domain.Entities;

public sealed class Vessel : ISynchronizableEntity
{
    public Guid Id { get; set; }
    public required string VesselName { get; set; }
    public required string ImoNumber { get; set; }
    public string? Flag { get; set; }
    public string? CompanyName { get; set; }
    public string? InvoicingDetails { get; set; }
    public string? AccountingEmail { get; set; }
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }

    public ICollection<UserVessel> UserAssignments { get; set; } =
        new List<UserVessel>();

    public ICollection<Equipment> Equipment { get; set; } =
        new List<Equipment>();
}
