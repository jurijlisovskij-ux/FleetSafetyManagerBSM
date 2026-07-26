namespace FSM.Domain.Entities;

public sealed class Vessel
{
    public Guid Id { get; set; }
    public required string VesselName { get; set; }
    public required string ImoNumber { get; set; }
    public string? Flag { get; set; }
    public string? CompanyName { get; set; }
    public string? InvoicingDetails { get; set; }
    public string? AccountingEmail { get; set; }

    public ICollection<UserVessel> UserAssignments { get; set; } =
        new List<UserVessel>();

    public ICollection<Equipment> Equipment { get; set; } =
        new List<Equipment>();

    public ICollection<InspectionReport> InspectionReports { get; set; } =
        new List<InspectionReport>();
    public ICollection<ServiceRequest> ServiceRequests { get; set; } =
        new List<ServiceRequest>();
}
