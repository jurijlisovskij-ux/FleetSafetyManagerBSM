namespace FSM.Domain.Entities;

public sealed class UserVessel : ISynchronizableEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
