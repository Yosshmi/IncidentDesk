using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IncidentDesk.Api.Auth;
using IncidentDesk.Api.Persistence;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthenticationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Login_refresh_and_current_user_use_real_identity_tokens()
    {
        var login = await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "engineer1@example.test", password = DemoSeeder.Password }, TestContext.Current.CancellationToken);
        var tokens = await ApiFixture.ReadAsync<TokenResponse>(login, HttpStatusCode.OK);
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal(900, tokens.ExpiresIn);
        Assert.NotEmpty(tokens.AccessToken);
        Assert.NotEmpty(tokens.RefreshToken);

        var refresh = await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new { tokens.RefreshToken }, TestContext.Current.CancellationToken);
        var renewed = await ApiFixture.ReadAsync<TokenResponse>(refresh, HttpStatusCode.OK);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", renewed.AccessToken);
        var me = await ApiFixture.ReadAsync<CurrentUserResponse>(await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(DemoSeeder.EngineerOneId, me.Id);
        Assert.Equal("engineer1@example.test", me.Email);
        Assert.Equal([Roles.Engineer], me.Roles);
    }

    [Fact]
    public async Task Invalid_credentials_and_refresh_tokens_return_clear_errors()
    {
        await ApiFixture.ProblemAsync(await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "missing@example.test", password = "Incorrect1!" }, TestContext.Current.CancellationToken), 401, "invalid_credentials");
        await ApiFixture.ProblemAsync(await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = "not-a-protected-ticket" }, TestContext.Current.CancellationToken), 401, "invalid_refresh_token");
        await ApiFixture.ProblemAsync(await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "invalid-address", password = "" }, TestContext.Current.CancellationToken), 400);
        await ApiFixture.ProblemAsync(await fixture.Anonymous.GetAsync("/api/v1/incidents", TestContext.Current.CancellationToken), 401);

        using var invalidBearer = fixture.Factory.CreateClient();
        invalidBearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        await ApiFixture.ProblemAsync(await invalidBearer.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken), 401);
    }

    [Theory]
    [InlineData("register")]
    [InlineData("forgotPassword")]
    [InlineData("resetPassword")]
    public async Task Unrequested_identity_routes_are_not_exposed(string route)
    {
        var response = await fixture.Anonymous.PostAsJsonAsync($"/api/v1/auth/{route}", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Engineer_directory_exposes_only_engineers_and_checks_role_and_permissions()
    {
        var engineers = await ApiFixture.ReadAsync<UserDirectoryResponse[]>(
            await fixture.Engineer.GetAsync("/api/v1/users?role=Engineer", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(2, engineers.Length);
        Assert.Contains(engineers, user => user.Id == DemoSeeder.EngineerOneId);
        Assert.Contains(engineers, user => user.Id == DemoSeeder.EngineerTwoId);
        Assert.DoesNotContain(engineers, user => user.Id == DemoSeeder.ReporterOneId);
        await ApiFixture.ProblemAsync(await fixture.Reporter.GetAsync("/api/v1/users?role=Engineer", TestContext.Current.CancellationToken), 403);
        await ApiFixture.ProblemAsync(await fixture.Engineer.GetAsync("/api/v1/users?role=Reporter", TestContext.Current.CancellationToken), 400, "invalid_role");
    }

    [Fact]
    public async Task Health_probes_report_live_process_and_ready_database()
    {
        Assert.Equal(HttpStatusCode.OK, (await fixture.Anonymous.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Anonymous.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Refresh_rejects_expired_and_revoked_tickets_and_login_enforces_lockout()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"auth-{Guid.NewGuid():N}@example.test";
        var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email, DisplayName = "Auth test", LockoutEnabled = true };
        Assert.True((await users.CreateAsync(user, DemoSeeder.Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, Roles.Reporter)).Succeeded);
        var tokens = await ApiFixture.ReadAsync<TokenResponse>(await fixture.Anonymous.PostAsJsonAsync(
            "/api/v1/auth/login", new { email, password = DemoSeeder.Password }, TestContext.Current.CancellationToken), HttpStatusCode.OK);

        var protector = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>()
            .Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
        var expired = protector.Unprotect(tokens.RefreshToken);
        Assert.NotNull(expired);
        expired.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await ApiFixture.ProblemAsync(await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = protector.Protect(expired) }, TestContext.Current.CancellationToken), 401, "invalid_refresh_token");

        Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        await ApiFixture.ProblemAsync(await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new { tokens.RefreshToken }, TestContext.Current.CancellationToken), 401, "invalid_refresh_token");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await ApiFixture.ProblemAsync(await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/login",
                new { email, password = "WrongPassword1!" }, TestContext.Current.CancellationToken), 401, "invalid_credentials");
        }
        await ApiFixture.ProblemAsync(await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = DemoSeeder.Password }, TestContext.Current.CancellationToken), 401, "invalid_credentials");
        var db = scope.ServiceProvider.GetRequiredService<IncidentDbContext>();
        await db.Entry(user).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(await users.IsLockedOutAsync(user));
    }

    [Fact]
    public async Task Error_responses_remain_JSON_when_the_client_accepts_plain_text()
    {
        using var anonymous = fixture.Factory.CreateClient();
        anonymous.DefaultRequestHeaders.Accept.ParseAdd("text/plain");
        await ApiFixture.ProblemAsync(await anonymous.GetAsync("/api/v1/incidents", TestContext.Current.CancellationToken), 401);
        using var engineer = fixture.Factory.CreateClient();
        engineer.DefaultRequestHeaders.Authorization = fixture.Engineer.DefaultRequestHeaders.Authorization;
        engineer.DefaultRequestHeaders.Accept.ParseAdd("text/plain");
        await ApiFixture.ProblemAsync(await engineer.GetAsync($"/api/v1/incidents/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken), 404, "incident_not_found");
    }
}
