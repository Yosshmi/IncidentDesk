using System.ComponentModel.DataAnnotations;
using IncidentDesk.Api.Common;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace IncidentDesk.Api.Auth;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    SignInManager<AppUser> signInManager,
    UserManager<AppUser> userManager,
    IOptionsMonitor<BearerTokenOptions> bearerOptions,
    TimeProvider timeProvider) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    [ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        if (request.Email.Contains('\0', StringComparison.Ordinal))
        {
            throw new ApiException(400, "invalid_input", "Email cannot contain a null character.", "email");
        }
        Response.Headers.CacheControl = "no-store";
        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signInManager.PasswordSignInAsync(
            request.Email.Trim(), request.Password, isPersistent: false, lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            // Use the same response for an unknown account, wrong password, or locked account.
            throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_credentials",
                "The email or password is invalid, or the account is temporarily locked.");
        }

        // Identity's bearer handler has already written access/refresh tokens to the response.
        return new EmptyResult();
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("refresh")]
    [ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(RefreshRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        var ticket = bearerOptions.Get(IdentityConstants.BearerScheme)
            .RefreshTokenProtector.Unprotect(request.RefreshToken);

        if (ticket?.Properties.ExpiresUtc is not { } expiry ||
            timeProvider.GetUtcNow() >= expiry ||
            await signInManager.ValidateSecurityStampAsync(ticket.Principal) is not { } user ||
            await userManager.IsLockedOutAsync(user))
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_refresh_token",
                "The refresh token is invalid or expired. Sign in again.");
        }

        var principal = await signInManager.CreateUserPrincipalAsync(user);
        return SignIn(principal, IdentityConstants.BearerScheme);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserResponse>> Me()
    {
        var user = await userManager.GetUserAsync(User)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "user_unavailable",
                "The account is unavailable. Sign in again.");
        var roles = await userManager.GetRolesAsync(user);
        return Ok(new CurrentUserResponse(user.Id, user.Email!, user.DisplayName,
            roles.OrderBy(role => role, StringComparer.Ordinal).ToArray()));
    }
}

public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(128)]
    public string Password { get; init; } = string.Empty;
}

public sealed class RefreshRequest
{
    [Required, StringLength(16384)]
    public string RefreshToken { get; init; } = string.Empty;
}

public sealed record CurrentUserResponse(Guid Id, string Email, string DisplayName, string[] Roles);
