using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

[ApiController]
[Authorize(Roles = "OfficeAdmin")]
[Route("api/audit")]
public sealed class AuditController(FsmDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => Ok(
        await db.AuditLogs.AsNoTracking().OrderByDescending(x => x.DateUtc)
            .Take(500).ToListAsync(cancellationToken));
}
