using FSM.Domain.Enums;

namespace FSM.Domain.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public required string FullName { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<UserVessel> VesselAssignments { get; set; } = new List<UserVessel>();
}
