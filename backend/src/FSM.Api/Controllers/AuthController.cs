using FSM.Api.Auth;
using FSM.Api.Services;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using FSM.Infrastructure.Security;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public sealed record LoginRequest(string? Username, string Password, string? Email = null);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record LocalRecoveryRequest(string NewPassword);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    FsmDbContext db,
    JwtTokenService jwtTokenService,
    IConfiguration configuration,
    AuditService audit,
    RecoveryKeyService recoveryKeys) : ControllerBase
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
            user.ForcePasswordChange
        });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        fullName = User.Identity?.Name
    });

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var id = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var user = await db.Users.SingleAsync(x => x.Id == id, cancellationToken);
        if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { message = "Current password is incorrect." });
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return BadRequest(new { message = "New password must contain at least 8 characters." });
        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        user.ForcePasswordChange = false;
        audit.Add("Password changed", user.Username, user.Username);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Password changed successfully." });
    }

    [Authorize(Roles = "OfficeAdmin")]
    [HttpGet("recovery-key")]
    public IActionResult ExportRecoveryKey() => File(recoveryKeys.Export(),
        "application/octet-stream", "FSM-Recovery.fsmkey");

    [Authorize(Roles = "OfficeAdmin")]
    [HttpPost("recovery-key/rotate")]
    public async Task<IActionResult> RotateRecoveryKey(CancellationToken cancellationToken)
    {
        var bytes = recoveryKeys.Rotate();
        audit.Add("Recovery key rotated", User.Identity?.Name ?? "OfficeAdmin", null);
        await db.SaveChangesAsync(cancellationToken);
        return File(bytes, "application/octet-stream", "FSM-Recovery.fsmkey");
    }

    [AllowAnonymous]
    [HttpPost("local-emergency-recovery")]
    public async Task<IActionResult> LocalEmergencyRecovery(
        LocalRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!(configuration["Application:Edition"] ?? "Office")
            .Equals("Office", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(403, new
            {
                message = "Administrator recovery is available only in Office Edition."
            });
        }

        var remoteAddress = HttpContext.Connection.RemoteIpAddress;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            return StatusCode(403, new
            {
                message = "Administrator recovery must be performed on the Office PC."
            });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return BadRequest(new { message = "New password must contain at least 8 characters." });

        var admin = await db.Users.SingleAsync(
            x => x.Role == UserRole.OfficeAdmin,
            cancellationToken);
        admin.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        admin.IsActive = true;
        admin.IsLocked = false;
        admin.FailedLoginAttempts = 0;
        admin.ForcePasswordChange = false;
        audit.Add("Local administrator recovery", "Office PC", admin.Username);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Password reset successfully. Sign in with the new password." });
    }

    [AllowAnonymous]
    [HttpPost("emergency-recovery")]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> EmergencyRecovery(IFormFile recoveryFile,
        [FromForm] string newPassword, CancellationToken cancellationToken)
    {
        if (!(configuration["Application:Edition"] ?? "Office").Equals("Office", StringComparison.OrdinalIgnoreCase))
            return StatusCode(403, new { message = "Administrator recovery is available only in Office Edition." });
        await using var stream = recoveryFile.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        if (!recoveryKeys.Validate(memory.ToArray()))
            return Unauthorized(new { message = "The recovery key is invalid or no longer active." });
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            return BadRequest(new { message = "New password must contain at least 8 characters." });
        var admin = await db.Users.SingleAsync(x => x.Role == UserRole.OfficeAdmin, cancellationToken);
        admin.PasswordHash = PasswordHasher.Hash(newPassword);
        admin.IsActive = true;
        admin.IsLocked = false;
        admin.FailedLoginAttempts = 0;
        admin.ForcePasswordChange = false;
        audit.Add("Emergency recovery", "Local recovery key", admin.Username);
        audit.Add("Administrator recovery", "Local recovery key", admin.Username);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Administrator password recovered successfully." });
    }
}
