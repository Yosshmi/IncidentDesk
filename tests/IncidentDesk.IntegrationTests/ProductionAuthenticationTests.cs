using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IncidentDesk.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ProductionAuthenticationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Database_protection_keys_preserve_access_and_refresh_tokens_across_replacement_hosts()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<IncidentDbContext>().Database.GetConnectionString()!;
        var directory = Path.Combine(Path.GetTempPath(), $"incidentdesk-keys-{Guid.NewGuid():N}");
        var previousDatabaseMode = Environment.GetEnvironmentVariable("DataProtection__PersistToDatabase");
        var previousKeyPath = Environment.GetEnvironmentVariable("DataProtection__KeyPath");
        try
        {
            Environment.SetEnvironmentVariable("DataProtection__PersistToDatabase", "true");
            Environment.SetEnvironmentVariable("DataProtection__KeyPath", Path.Combine(directory, "first"));
            TokenResponse tokens;
            await using (var first = CreateFactory(connection, Path.Combine(directory, "first")))
            {
                using var client = first.CreateClient();
                tokens = await ApiFixture.ReadAsync<TokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/login",
                    new { email = "engineer1@example.test", password = DemoSeeder.Password }, TestContext.Current.CancellationToken), HttpStatusCode.OK);
            }

            Environment.SetEnvironmentVariable("DataProtection__KeyPath", Path.Combine(directory, "replacement"));
            await using var replacement = CreateFactory(connection, Path.Combine(directory, "replacement"));
            using var authenticated = replacement.CreateClient();
            authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            Assert.Equal(HttpStatusCode.OK,
                (await authenticated.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
            using var anonymous = replacement.CreateClient();
            var refreshed = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { tokens.RefreshToken }, TestContext.Current.CancellationToken);
            await ApiFixture.ReadAsync<TokenResponse>(refreshed, HttpStatusCode.OK);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DataProtection__PersistToDatabase", previousDatabaseMode);
            Environment.SetEnvironmentVariable("DataProtection__KeyPath", previousKeyPath);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string connection, string keyPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:IncidentDesk"] = connection,
                    ["DataProtection:PersistToDatabase"] = "true",
                    ["DataProtection:KeyPath"] = keyPath,
                    ["Logging:LogLevel:Default"] = "Warning"
                }));
        });
}
