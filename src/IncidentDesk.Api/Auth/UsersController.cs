using IncidentDesk.Api.Common;
using IncidentDesk.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentDesk.Api.Auth;

[ApiController]
[Authorize(Roles = Roles.Engineer)]
[Route("api/v1/users")]
public sealed class UsersController(IncidentDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDirectoryResponse>>> List(
        CancellationToken cancellationToken, [FromQuery] string role = Roles.Engineer)
    {
        if (!string.Equals(role, Roles.Engineer, StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_role",
                "The user directory supports role=Engineer only.", "role");
        }

        var users = await (
                from user in db.Users.AsNoTracking()
                join membership in db.UserRoles on user.Id equals membership.UserId
                join userRole in db.Roles on membership.RoleId equals userRole.Id
                where userRole.Name == Roles.Engineer
                orderby user.DisplayName, user.Id
                select new UserDirectoryResponse(user.Id, user.DisplayName))
            .ToListAsync(cancellationToken);
        return Ok(users);
    }
}

public sealed record UserDirectoryResponse(Guid Id, string DisplayName);
