using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IncidentDesk.Api.Incidents;
using IncidentDesk.Api.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace IncidentDesk.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "PostgreSQL API";
}

public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6")
        .WithDatabase("incidentdesk_tests")
        .WithUsername("incidentdesk")
        .WithPassword("integration-tests-only")
        .Build();
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Anonymous { get; private set; } = null!;
    public HttpClient Reporter => _clients["reporter1"];
    public HttpClient OtherReporter => _clients["reporter2"];
    public HttpClient Engineer => _clients["engineer1"];
    public HttpClient OtherEngineer => _clients["engineer2"];

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:IncidentDesk"] = _postgres.GetConnectionString(),
                    ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
                    ["Logging:LogLevel:Default"] = "Warning"
                }));
        });
        Anonymous = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await Anonymous.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IncidentDbContext>();
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
        await DemoSeeder.SeedAsync(Factory.Services, TestContext.Current.CancellationToken);

        foreach (var name in new[] { "reporter1", "reporter2", "engineer1", "engineer2" })
        {
            var response = await Anonymous.PostAsJsonAsync("/api/v1/auth/login",
                new { email = $"{name}@example.test", password = DemoSeeder.Password }, TestContext.Current.CancellationToken);
            var token = await ReadAsync<TokenResponse>(response, HttpStatusCode.OK);
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            _clients.Add(name, client);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values) client.Dispose();
        Anonymous?.Dispose();
        if (Factory is not null) await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public async Task<VersionedIncident> CreateAsync(HttpClient? client = null, string? title = null,
        string severity = "Medium", string? description = null)
    {
        var response = await (client ?? Reporter).PostAsJsonAsync("/api/v1/incidents",
            new { title = title ?? $"Incident {Guid.NewGuid():N}", description = description ?? "Reproduction details for the integration scenario.", severity }, TestContext.Current.CancellationToken);
        var incident = await ReadAsync<IncidentResponse>(response, HttpStatusCode.Created);
        Assert.Equal($"/api/v1/incidents/{incident.Id}", response.Headers.Location?.AbsolutePath);
        return new VersionedIncident(incident, RequiredETag(response));
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
        object body, string? etag = null)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<VersionedIncident> MutateAsync(HttpClient client, HttpMethod method,
        VersionedIncident current, string suffix, object body)
    {
        var response = await SendAsync(client, method, $"/api/v1/incidents/{current.Value.Id}{suffix}", body, current.ETag);
        var incident = await ReadAsync<IncidentResponse>(response, HttpStatusCode.OK);
        var etag = RequiredETag(response);
        Assert.NotEqual(current.ETag, etag);
        return new VersionedIncident(incident, etag);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, received {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, JsonOptions)
            ?? throw new InvalidOperationException("The API returned an empty JSON document.");
    }

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, int status, string? code = null)
    {
        var problem = await ReadAsync<JsonElement>(response, (HttpStatusCode)status);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        if (code is not null) Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    public static string RequiredETag(HttpResponseMessage response)
    {
        var etag = response.Headers.ETag;
        Assert.NotNull(etag);
        Assert.False(etag.IsWeak);
        Assert.True(Guid.TryParse(etag.Tag.Trim('"'), out _));
        return etag.ToString();
    }
}

public sealed record VersionedIncident(IncidentResponse Value, string ETag);
public sealed record TokenResponse(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken);
