using FSM.Api.Auth;
using FSM.Api.Services;
using FSM.Infrastructure.Persistence;
using FSM.Infrastructure.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public sealed record LoginRequest(string Email, string Password);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    FsmDbContext db,
    JwtTokenService jwtTokenService,
    SystemAuditService audit) : ControllerBase
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

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName)
        ]);
        await audit.WriteAsync(new ClaimsPrincipal(identity), "Login", "Session",
            null, "Successful login.", cancellationToken);

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

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await audit.WriteAsync(User, "Logout", "Session", null,
            "User signed out.", ct);
        return NoContent();
    }
}
