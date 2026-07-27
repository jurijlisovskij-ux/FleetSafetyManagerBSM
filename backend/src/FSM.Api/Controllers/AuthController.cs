using FSM.Api.Auth;
using FSM.Infrastructure.Persistence;
using FSM.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public sealed record LoginRequest(string Email, string Password);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    FsmDbContext db,
    JwtTokenService jwtTokenService) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Email.ToLower() == email,
                cancellationToken);

        if (user is null ||
            !user.IsActive ||
            !PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized(new { message = "Invalid email or password." });
        }

        return Ok(new
        {
            token = jwtTokenService.Create(user),
            userId = user.Id,
            user.FullName,
            user.Email,
            role = user.Role.ToString()
        });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        fullName = User.Identity?.Name
    });
}
