using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;

namespace FSM.Api.Services;

public sealed class AuditService(FsmDbContext db)
{
    public void Add(string action, string operatorName, string? targetUser,
        string result = "Success", string? summary = null) => db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        DateUtc = DateTime.UtcNow,
        Action = action,
        Operator = operatorName,
        TargetUser = targetUser,
        ComputerName = Environment.MachineName,
        Result = result,
        Summary = summary
    });
}
