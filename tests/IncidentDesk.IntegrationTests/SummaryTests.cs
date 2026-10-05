using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IncidentDesk.Api.Common;
using IncidentDesk.Api.Incidents;
using IncidentDesk.Api.Persistence;
using IncidentDesk.Api.Summaries;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IncidentDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class SummaryTests(ApiFixture fixture)
{
    [Fact]
    public async Task Draft_uses_persisted_notes_without_saving_until_an_engineer_reviews_it()
    {
        var incident = await fixture.CreateAsync(title: "Sandbox queue delay", description: "Confirmation events arrive late.");
        incident = await AddNoteAsync(incident, "Observed a queue backlog during the sandbox import.");
        incident = await AddNoteAsync(incident, "The cause has not yet been confirmed.");
        var path = $"/api/v1/incidents/{incident.Value.Id}";
        var before = await ApiFixture.ReadAsync<IncidentResponse>(
            await fixture.Engineer.GetAsync(path, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        var generator = new FakeSummaryGenerator((_, _) => Task.FromResult("Draft: queue delays observed; cause remains unknown."));
        await using var factory = WithGenerator(generator);
        using var engineer = await LoginAsync(factory, "engineer1");

        var response = await engineer.PostAsync(path + "/summary-draft", null, TestContext.Current.CancellationToken);
        var draft = await ApiFixture.ReadAsync<SummaryDraftResponse>(response, HttpStatusCode.OK);
        Assert.Equal(incident.ETag, ApiFixture.RequiredETag(response));
        Assert.Equal(before.Version, draft.SourceVersion);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(1, generator.Calls);
        Assert.False(generator.HadOpenTransaction);
        var input = Assert.IsType<SummaryInput>(generator.LastInput);
        Assert.Equal(before.Title, input.Title);
        Assert.Equal(before.Description, input.Description);
        Assert.Equal("Open", input.Status);
        Assert.Null(input.ResolutionNote);
        Assert.Equal(["Observed a queue backlog during the sandbox import.", "The cause has not yet been confirmed."],
            input.Notes.Select(note => note.Body));
        Assert.True(input.Notes[0].CreatedAt <= input.Notes[1].CreatedAt);

        var unchanged = await ApiFixture.ReadAsync<IncidentResponse>(
            await fixture.Engineer.GetAsync(path, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(before, unchanged);
        var history = await ApiFixture.ReadAsync<PagedResult<HistoryResponse>>(
            await fixture.Engineer.GetAsync(path + "/history", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Single(history.Items);
        var notes = await ApiFixture.ReadAsync<PagedResult<CommentResponse>>(
            await fixture.Engineer.GetAsync(path + "/comments", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(2, notes.TotalCount);

        var reviewed = "Engineer reviewed: " + draft.Text;
        var saved = await ApiFixture.MutateAsync(engineer, HttpMethod.Put, incident, "/summary", new { text = reviewed });
        Assert.Equal(reviewed, saved.Value.Summary);
        Assert.Equal(DemoSeeder.EngineerOneId, saved.Value.SummaryReviewedById);
        Assert.NotNull(saved.Value.SummaryReviewedAt);
        Assert.NotEqual(draft.SourceVersion, saved.Value.Version);
    }

    [Fact]
    public async Task Note_added_while_generation_is_waiting_leaves_the_draft_stale_and_unsaved()
    {
        var incident = await fixture.CreateAsync();
        incident = await AddNoteAsync(incident, "Original investigation note.");
        var path = $"/api/v1/incidents/{incident.Value.Id}";
        var started = new TaskCompletionSource<SummaryInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeSummaryGenerator(async (input, cancellationToken) =>
        {
            started.TrySetResult(input);
            return await completion.Task.WaitAsync(cancellationToken);
        });
        await using var factory = WithGenerator(generator);
        using var engineer = await LoginAsync(factory, "engineer1");
        var pending = engineer.PostAsync(path + "/summary-draft", null, TestContext.Current.CancellationToken);
        VersionedIncident changed;
        try
        {
            var snapshot = await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("Original investigation note.", Assert.Single(snapshot.Notes).Body);
            Assert.False(generator.HadOpenTransaction);
            changed = await AddNoteAsync(incident, "New evidence arrived while generation was in progress.");
        }
        finally
        {
            completion.TrySetResult("Draft based on the original note only.");
        }

        var response = await pending;
        var draft = await ApiFixture.ReadAsync<SummaryDraftResponse>(response, HttpStatusCode.OK);
        var sourceETag = ApiFixture.RequiredETag(response);
        Assert.Equal(incident.ETag, sourceETag);
        Assert.NotEqual(changed.ETag, sourceETag);
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(engineer, HttpMethod.Put, path + "/summary",
            new { text = draft.Text }, sourceETag), 412, "version_conflict");
        var latestResponse = await fixture.Engineer.GetAsync(path, TestContext.Current.CancellationToken);
        var latest = await ApiFixture.ReadAsync<IncidentResponse>(latestResponse, HttpStatusCode.OK);
        Assert.Equal(changed.ETag, ApiFixture.RequiredETag(latestResponse));
        Assert.Null(latest.Summary);
        Assert.Null(latest.SummaryReviewedById);
        Assert.Null(latest.SummaryReviewedAt);
        var notes = await ApiFixture.ReadAsync<PagedResult<CommentResponse>>(
            await fixture.Engineer.GetAsync(path + "/comments", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(2, notes.TotalCount);
        var history = await ApiFixture.ReadAsync<PagedResult<HistoryResponse>>(
            await fixture.Engineer.GetAsync(path + "/history", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Single(history.Items);
    }

    [Fact]
    public async Task Disabled_AI_returns_503_and_manual_summaries_remain_available()
    {
        var incident = await fixture.CreateAsync();
        incident = await AddNoteAsync(incident, "Investigation can continue without an AI provider.");
        var response = await fixture.Engineer.PostAsync($"/api/v1/incidents/{incident.Value.Id}/summary-draft",
            null, TestContext.Current.CancellationToken);
        await ApiFixture.ProblemAsync(response, 503, "summary_unavailable");
        var saved = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Put, incident, "/summary",
            new { text = "Manually written and reviewed summary." });
        Assert.Equal("Manually written and reviewed summary.", saved.Value.Summary);
    }

    [Theory]
    [InlineData(502, "summary_provider_failed")]
    [InlineData(504, "summary_timeout")]
    public async Task Provider_failures_leave_data_unchanged_and_core_edits_work(int status, string code)
    {
        var incident = await fixture.CreateAsync();
        incident = await AddNoteAsync(incident, "Observation for a provider failure scenario.");
        var path = $"/api/v1/incidents/{incident.Value.Id}";
        var before = await ApiFixture.ReadAsync<IncidentResponse>(
            await fixture.Engineer.GetAsync(path, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        var generator = new FakeSummaryGenerator((_, _) => Task.FromException<string>(
            new ApiException(status, code, "The summary provider is unavailable.")));
        await using var factory = WithGenerator(generator);
        using var engineer = await LoginAsync(factory, "engineer1");
        await ApiFixture.ProblemAsync(await engineer.PostAsync(path + "/summary-draft", null,
            TestContext.Current.CancellationToken), status, code);
        var after = await ApiFixture.ReadAsync<IncidentResponse>(
            await fixture.Engineer.GetAsync(path, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(before, after);
        var edited = await ApiFixture.MutateAsync(engineer, HttpMethod.Put, incident, "",
            new { title = "Investigation continues", description = before.Description, severity = "High" });
        Assert.Equal("Investigation continues", edited.Value.Title);
        Assert.Null(edited.Value.Summary);
    }

    [Fact]
    public async Task Reporter_and_missing_incident_requests_never_invoke_the_provider()
    {
        var incident = await fixture.CreateAsync();
        var generator = new FakeSummaryGenerator((_, _) => Task.FromResult("Must not run."));
        await using var factory = WithGenerator(generator);
        using var reporter = await LoginAsync(factory, "reporter1");
        using var engineer = await LoginAsync(factory, "engineer1");
        await ApiFixture.ProblemAsync(await reporter.PostAsync($"/api/v1/incidents/{incident.Value.Id}/summary-draft",
            null, TestContext.Current.CancellationToken), 403);
        await ApiFixture.ProblemAsync(await engineer.PostAsync($"/api/v1/incidents/{Guid.NewGuid()}/summary-draft",
            null, TestContext.Current.CancellationToken), 404, "incident_not_found");
        Assert.Equal(0, generator.Calls);
    }

    private WebApplicationFactory<Program> WithGenerator(FakeSummaryGenerator generator)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIncidentSummaryGenerator>();
            services.AddScoped<IIncidentSummaryGenerator>(provider =>
            {
                var db = provider.GetRequiredService<IncidentDbContext>();
                return new FakeSummaryGenerator((input, cancellationToken) =>
                {
                    generator.HadOpenTransaction = db.Database.CurrentTransaction is not null;
                    return generator.GenerateAsync(input, cancellationToken);
                });
            });
        }));

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory, string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = $"{name}@example.test", password = DemoSeeder.Password }, TestContext.Current.CancellationToken);
        var token = await ApiFixture.ReadAsync<TokenResponse>(response, HttpStatusCode.OK);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private async Task<VersionedIncident> AddNoteAsync(VersionedIncident incident, string body)
    {
        var response = await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Post,
            $"/api/v1/incidents/{incident.Value.Id}/comments", new { body }, incident.ETag);
        await ApiFixture.ReadAsync<CommentResponse>(response, HttpStatusCode.Created);
        return incident with { ETag = ApiFixture.RequiredETag(response) };
    }

    private sealed class FakeSummaryGenerator(Func<SummaryInput, CancellationToken, Task<string>> generate)
        : IIncidentSummaryGenerator
    {
        public int Calls { get; private set; }
        public SummaryInput? LastInput { get; private set; }
        public bool? HadOpenTransaction { get; set; }

        public Task<string> GenerateAsync(SummaryInput input, CancellationToken cancellationToken)
        {
            Calls++;
            LastInput = input;
            return generate(input, cancellationToken);
        }
    }
}
