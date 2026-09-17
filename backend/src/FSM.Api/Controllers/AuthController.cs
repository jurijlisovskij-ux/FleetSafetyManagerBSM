using FSM.Api.Auth;
using FSM.Api.Services;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using FSM.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public sealed record LoginRequest(string? Username, string Password, string? Email = null);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    FsmDbContext db,
    JwtTokenService jwtTokenService,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var suppliedUsername = string.IsNullOrWhiteSpace(request.Username)
            ? request.Email
            : request.Username;
        if (string.IsNullOrWhiteSpace(suppliedUsername) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized(new { message = "Invalid username or password." });
        }

        var username = suppliedUsername.Trim().ToLowerInvariant();

        var user = await db.Users
            .SingleOrDefaultAsync(
                x => x.Username.ToLower() == username,
                cancellationToken);

        if (user is null || !user.IsActive || user.IsLocked)
        {
            return Unauthorized(new { message = "Invalid username or password." });
        }
        if (!PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= 5) user.IsLocked = true;
            await db.SaveChangesAsync(cancellationToken);
            return Unauthorized(new { message = "Invalid username or password." });
        }
        user.FailedLoginAttempts = 0;
        await db.SaveChangesAsync(cancellationToken);

        var edition = configuration["Application:Edition"] ?? "Office";
        var isShipEdition = edition.Equals("Ship", StringComparison.OrdinalIgnoreCase);
        var roleAllowed = isShipEdition
            ? user.Role == UserRole.Crew
            : user.Role is UserRole.OfficeAdmin or UserRole.Superintendent;

        if (!roleAllowed)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = isShipEdition
                    ? "This account is not permitted to use the Ship edition."
                    : "This account is not permitted to use the Office edition."
            });
        }

        return Ok(new
        {
            token = jwtTokenService.Create(user),
            userId = user.Id,
            user.Username,
            user.FullName,
            user.Email,
            role = user.Role.ToString(),
            edition,
            ForcePasswordChange = false
        });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        fullName = User.Identity?.Name
    });
}
