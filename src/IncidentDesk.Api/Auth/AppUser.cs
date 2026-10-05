using Microsoft.AspNetCore.Identity;

namespace IncidentDesk.Api.Auth;

public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
}
