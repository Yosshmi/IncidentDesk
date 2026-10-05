using System.Security.Claims;

namespace IncidentDesk.Api.Common;

public static class CurrentUser
{
    public static Guid Id(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty
            ? id
            : throw new ApiException(401, "invalid_identity", "Sign in to continue.");

    public static bool IsEngineer(ClaimsPrincipal principal) => principal.IsInRole("Engineer");
}
