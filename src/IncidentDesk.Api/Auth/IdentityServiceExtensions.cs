using IncidentDesk.Api.Persistence;
using Microsoft.AspNetCore.Identity;

namespace IncidentDesk.Api.Auth;

public static class IdentityServiceExtensions
{
    public static IServiceCollection AddIncidentDeskIdentity(this IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.BearerScheme)
            .AddBearerToken(IdentityConstants.BearerScheme, options =>
            {
                options.BearerTokenExpiration = TimeSpan.FromMinutes(15);
                options.RefreshTokenExpiration = TimeSpan.FromDays(7);
            });

        services.AddAuthorization();
        services.AddIdentityCore<AppUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<IncidentDbContext>()
            .AddSignInManager();

        return services;
    }
}
