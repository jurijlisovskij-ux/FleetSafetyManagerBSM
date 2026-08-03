namespace FSM.Domain.Entities;

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public DateTime DateUtc { get; set; }
    public required string Action { get; set; }
    public required string Operator { get; set; }
    public string? TargetUser { get; set; }
    public required string ComputerName { get; set; }
    public required string Result { get; set; }
    public string? Summary { get; set; }
}
