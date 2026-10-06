using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IncidentDesk.Api.Incidents;
using IncidentDesk.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class TimestampTests(ApiFixture fixture)
{
    [Fact]
    public async Task Creation_response_and_history_match_the_record_read_from_PostgreSQL_with_a_precise_clock()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<TimeProvider>(new PreciseTimeProvider())));
        using var client = factory.CreateClient();
        var login = await ApiFixture.ReadAsync<TokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "reporter1@example.test", password = DemoSeeder.Password },
            TestContext.Current.CancellationToken), HttpStatusCode.OK);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var created = await fixture.CreateAsync(client);
        var path = $"/api/v1/incidents/{created.Value.Id}";
        var persisted = await ApiFixture.ReadAsync<IncidentResponse>(
            await client.GetAsync(path, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        var history = await ApiFixture.ReadAsync<PagedResult<HistoryResponse>>(
            await client.GetAsync(path + "/history", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        var expected = new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

        Assert.Equal(expected, created.Value.CreatedAt);
        Assert.Equal(expected, created.Value.UpdatedAt);
        Assert.Equal(created.Value, persisted);
        Assert.Equal(expected, Assert.Single(history.Items).CreatedAt);
    }

    private sealed class PreciseTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
            => new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
    }
}
