namespace FSM.Domain.Entities;

public sealed class UserVessel
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid VesselId { get; set; }
    public Vessel? Vessel { get; set; }
}
