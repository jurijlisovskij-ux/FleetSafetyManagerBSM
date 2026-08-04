using FSM.Domain.Enums;

namespace FSM.Domain.Entities;

public sealed class User : ISynchronizableEntity
{
    public Guid Id { get; set; }
    public required string FullName { get; set; }
    public required string Username { get; set; }
    public string? Email { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public bool ForcePasswordChange { get; set; }
    public bool IsLocked { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public ICollection<UserVessel> VesselAssignments { get; set; } = new List<UserVessel>();
}
