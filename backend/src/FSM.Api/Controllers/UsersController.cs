using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using FSM.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSM.Api.Controllers;

public sealed record SaveUserRequest(
    string FullName,
    string Email,
    UserRole Role,
    bool IsActive,
    string? Password,
    IReadOnlyList<Guid> VesselIds);

[ApiController]
[Authorize(Roles = "OfficeAdmin")]
[Route("api/users")]
public sealed class UsersController(FsmDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .OrderBy(x => x.FullName)
            .Select(x => new
            {
                x.Id,
                x.FullName,
                x.Email,
                Role = x.Role.ToString(),
                x.IsActive,
                VesselIds = x.VesselAssignments
                    .Select(a => a.VesselId)
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        SaveUserRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(
            request,
            null,
            cancellationToken);

        if (validation is not null)
            return validation;

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new
            {
                message =
                    "Password is required for a new user."
            });
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = request.FullName.Trim(),
            Email = request.Email.Trim().ToLowerInvariant(),
            PasswordHash =
                PasswordHasher.Hash(request.Password),
            Role = request.Role,
            IsActive = request.IsActive
        };

        db.Users.Add(user);

        if (request.Role != UserRole.OfficeAdmin)
        {
            foreach (var vesselId in request.VesselIds.Distinct())
            {
                db.UserVessels.Add(new UserVessel
                {
                    UserId = user.Id,
                    VesselId = vesselId
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return Created(
            $"/api/users/{user.Id}",
            ToResponse(user, request.VesselIds));
    }

    [HttpPut("{userId:guid}")]
    public async Task<IActionResult> Update(
        Guid userId,
        SaveUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .Include(x => x.VesselAssignments)
            .SingleOrDefaultAsync(
                x => x.Id == userId,
                cancellationToken);

        if (user is null)
            return NotFound();

        var validation = await ValidateAsync(
            request,
            userId,
            cancellationToken);

        if (validation is not null)
            return validation;

        user.FullName = request.FullName.Trim();
        user.Email =
            request.Email.Trim().ToLowerInvariant();
        user.Role = request.Role;
        user.IsActive = request.IsActive;

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            user.PasswordHash =
                PasswordHasher.Hash(request.Password);
        }

        db.UserVessels.RemoveRange(
            user.VesselAssignments);

        if (request.Role != UserRole.OfficeAdmin)
        {
            foreach (var vesselId in request.VesselIds.Distinct())
            {
                db.UserVessels.Add(new UserVessel
                {
                    UserId = user.Id,
                    VesselId = vesselId
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return Ok(ToResponse(
            user,
            request.Role == UserRole.OfficeAdmin
                ? Array.Empty<Guid>()
                : request.VesselIds));
    }

    private async Task<IActionResult?> ValidateAsync(
        SaveUserRequest request,
        Guid? existingUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            return BadRequest(new
            {
                message = "Full Name is required."
            });
        }

        var email = request.Email.Trim();

        if (string.IsNullOrWhiteSpace(email) ||
            !email.Contains('@'))
        {
            return BadRequest(new
            {
                message =
                    "A valid Email is required."
            });
        }

        if (await db.Users.AnyAsync(
            x => x.Id != existingUserId &&
                 x.Email.ToLower() ==
                    email.ToLower(),
            cancellationToken))
        {
            return Conflict(new
            {
                message =
                    "A user with this Email already exists."
            });
        }

        if (request.Role != UserRole.OfficeAdmin)
        {
            var vesselIds = request.VesselIds
                .Distinct()
                .ToList();

            var existingCount = await db.Vessels
                .CountAsync(
                    x => vesselIds.Contains(x.Id),
                    cancellationToken);

            if (existingCount != vesselIds.Count)
            {
                return BadRequest(new
                {
                    message =
                        "One or more selected vessels are invalid."
                });
            }
        }

        return null;
    }

    private static object ToResponse(
        User user,
        IEnumerable<Guid> vesselIds) =>
        new
        {
            user.Id,
            user.FullName,
            user.Email,
            Role = user.Role.ToString(),
            user.IsActive,
            VesselIds = vesselIds.Distinct().ToList()
        };
}
