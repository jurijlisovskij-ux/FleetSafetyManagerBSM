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
public sealed record AdminRecoveryRequest(string Email);
public sealed record AdminRecoveryConfirmRequest(
    string Email,
    string Code,
    string NewPassword);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    FsmDbContext db,
    JwtTokenService jwtTokenService,
    IConfiguration configuration,
    AuditService audit,
    EmailSender emailSender,
    AdminPasswordRecoveryService passwordRecovery) : ControllerBase
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

    [AllowAnonymous]
    [HttpPost("administrator-recovery/request")]
    public async Task<IActionResult> RequestAdministratorRecovery(
        AdminRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        var restriction = ValidateRecoveryEnvironment();
        if (restriction is not null) return restriction;
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return BadRequest(new { message = "Enter the registered administrator email." });
        if (!emailSender.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Administrator email recovery is not configured. Configure the company SMTP server first."
            });

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var admin = await db.Users.AsNoTracking().SingleOrDefaultAsync(
            x => x.Role == UserRole.OfficeAdmin,
            cancellationToken);

        // Always return the same result for an unknown email.
        if (admin is null || string.IsNullOrWhiteSpace(admin.Email) ||
            !string.Equals(admin.Email.Trim(), normalizedEmail,
                StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { message = RecoveryRequestMessage });
        }

        var issued = passwordRecovery.Issue(normalizedEmail);
        var safeCode = WebUtility.HtmlEncode(issued.Code);
        await emailSender.SendAsync(
            normalizedEmail,
            "Fleet Safety Manager administrator password code",
            $"<p>Your Fleet Safety Manager verification code is:</p><p style=\"font-size:28px;font-weight:bold;letter-spacing:4px\">{safeCode}</p><p>This code expires in 10 minutes and can be used once.</p>",
            cancellationToken);
        audit.Add("Administrator recovery code sent", "Office PC", admin.Username);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = RecoveryRequestMessage });
    }

    [AllowAnonymous]
    [HttpPost("administrator-recovery/confirm")]
    public async Task<IActionResult> ConfirmAdministratorRecovery(
        AdminRecoveryConfirmRequest request,
        CancellationToken cancellationToken)
    {
        var restriction = ValidateRecoveryEnvironment();
        if (restriction is not null) return restriction;
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return BadRequest(new { message = "New password must contain at least 8 characters." });
        var admin = await db.Users.SingleOrDefaultAsync(
            x => x.Role == UserRole.OfficeAdmin,
            cancellationToken);
        if (admin is null || string.IsNullOrWhiteSpace(admin.Email) ||
            !string.Equals(admin.Email.Trim(), request.Email.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !passwordRecovery.TryConsume(request.Email, request.Code))
        {
            return Unauthorized(new { message = "The verification code is invalid or expired." });
        }

        admin.PasswordHash = PasswordHasher.Hash(newPassword);
        admin.IsActive = true;
        admin.IsLocked = false;
        admin.FailedLoginAttempts = 0;
        admin.ForcePasswordChange = false;
        audit.Add("Administrator password recovered by email", "Office PC", admin.Username);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Administrator password changed. Sign in with the new password." });
    }

    private IActionResult? ValidateRecoveryEnvironment()
    {
        if (!(configuration["Application:Edition"] ?? "Office")
            .Equals("Office", StringComparison.OrdinalIgnoreCase))
            return StatusCode(403, new { message = "Administrator recovery is available only in Office Edition." });

        var address = HttpContext.Connection.RemoteIpAddress;
        return address is null || !IPAddress.IsLoopback(address)
            ? StatusCode(403, new { message = "Administrator recovery must be performed on the Office PC." })
            : null;
    }

    private const string RecoveryRequestMessage =
        "If this email is registered for OfficeAdmin, a 6-digit code has been sent.";
}
