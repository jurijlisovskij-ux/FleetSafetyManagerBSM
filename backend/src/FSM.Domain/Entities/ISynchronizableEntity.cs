namespace FSM.Domain.Entities;

public interface ISynchronizableEntity
{
    DateTime LastModifiedUtc { get; set; }
    bool IsDeleted { get; set; }
}
