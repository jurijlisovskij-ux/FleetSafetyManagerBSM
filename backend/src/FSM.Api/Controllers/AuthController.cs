using FSM.Api.Auth;
using FSM.Domain.Enums;
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
    JwtTokenService jwtTokenService,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized(new { message = "Invalid email or password." });
        }

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
            user.FullName,
            user.Email,
            role = user.Role.ToString(),
            edition
        });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        fullName = User.Identity?.Name
    });
}
