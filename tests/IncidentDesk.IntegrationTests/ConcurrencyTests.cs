using System.Net;
using IncidentDesk.Api.Incidents;
using IncidentDesk.Api.Persistence;
using IncidentDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ConcurrencyTests(ApiFixture fixture)
{
    [Fact]
    public async Task Writes_require_one_current_strong_ETag_and_stale_requests_preserve_data()
    {
        var incident = await fixture.CreateAsync();
        var path = $"/api/v1/incidents/{incident.Value.Id}";
        using var response = await fixture.Engineer.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.True(response.Headers.CacheControl?.NoTransform);
        Assert.False(response.Headers.ETag?.IsWeak);
        Assert.Equal(response.Headers.ETag!.ToString(), response.Headers.GetValues("X-Incident-ETag").Single());
        var change = new { title = "First accepted edit", description = "Updated description", severity = "High" };
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Put, path, change),
            428, "version_required");
        foreach (var malformed in new[] { "*", "not-a-version", $"W/{incident.ETag}", $"{incident.ETag}, {incident.ETag}" })
        {
            await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Put,
                path, change, malformed), 400, "invalid_version");
        }

        var updated = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Put, incident, "", change);
        Assert.NotEqual(incident.Value.Version, updated.Value.Version);
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.OtherEngineer, HttpMethod.Put,
            path, new { title = "Stale overwrite", description = "Stale", severity = "Low" }, incident.ETag), 412, "version_conflict");
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.OtherEngineer, HttpMethod.Post,
            path + "/comments", new { body = "Stale note" }, incident.ETag), 412, "version_conflict");
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.OtherEngineer, HttpMethod.Put,
            path + "/summary", new { text = "Stale draft" }, incident.ETag), 412, "version_conflict");
        var persisted = await ApiFixture.ReadAsync<IncidentResponse>(await fixture.Engineer.GetAsync(path, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(updated.Value, persisted);
        var notes = await ApiFixture.ReadAsync<PagedResult<CommentResponse>>(
            await fixture.Engineer.GetAsync(path + "/comments", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Empty(notes.Items);
    }

    [Fact]
    public async Task Simultaneous_HTTP_edits_accept_one_winner_and_return_412_for_the_other()
    {
        var incident = await fixture.CreateAsync();
        var path = $"/api/v1/incidents/{incident.Value.Id}";
        var writes = await Task.WhenAll(
            ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Put, path,
                new { title = "Engineer one", description = "One", severity = "High" }, incident.ETag),
            ApiFixture.SendAsync(fixture.OtherEngineer, HttpMethod.Put, path,
                new { title = "Engineer two", description = "Two", severity = "Low" }, incident.ETag));

        var winner = Assert.Single(writes, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(writes, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
        await ApiFixture.ProblemAsync(loser, 412, "version_conflict");
        var accepted = await ApiFixture.ReadAsync<IncidentResponse>(winner, HttpStatusCode.OK);
        var persisted = await ApiFixture.ReadAsync<IncidentResponse>(await fixture.Engineer.GetAsync(path, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(accepted, persisted);
    }

    [Fact]
    public async Task Competing_database_contexts_roll_back_losing_history_and_comment()
    {
        var incident = await fixture.CreateAsync();
        incident = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Put, incident, "/assignment",
            new { assigneeId = DemoSeeder.EngineerOneId });
        await using var scopeOne = fixture.Factory.Services.CreateAsyncScope();
        await using var scopeTwo = fixture.Factory.Services.CreateAsyncScope();
        var first = scopeOne.ServiceProvider.GetRequiredService<IncidentDbContext>();
        var second = scopeTwo.ServiceProvider.GetRequiredService<IncidentDbContext>();
        var firstCopy = await first.Incidents.SingleAsync(value => value.Id == incident.Value.Id, TestContext.Current.CancellationToken);
        var secondCopy = await second.Incidents.SingleAsync(value => value.Id == incident.Value.Id, TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        Prepare(first, firstCopy, "first competing note", DemoSeeder.EngineerOneId, now);
        Prepare(second, secondCopy, "second competing note", DemoSeeder.EngineerTwoId, now);

        var results = await Task.WhenAll(SaveAsync(first), SaveAsync(second));
        Assert.Single(results, result => result is null);
        Assert.IsType<DbUpdateConcurrencyException>(Assert.Single(results, result => result is not null));

        await using var verification = fixture.Factory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<IncidentDbContext>();
        var saved = await db.Incidents.SingleAsync(value => value.Id == incident.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(IncidentStatus.Investigating, saved.Status);
        var comment = Assert.Single(await db.Comments.Where(value => value.IncidentId == saved.Id).ToListAsync(TestContext.Current.CancellationToken));
        var history = await db.StatusHistory.Where(value => value.IncidentId == saved.Id)
            .OrderBy(value => value.CreatedAt).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, history.Count);
        Assert.Equal(comment.AuthorId, history[1].ActorId);
        Assert.Equal(comment.Body, history[1].Note);
    }

    private static void Prepare(IncidentDbContext db, Incident incident, string note, Guid actor, DateTimeOffset now)
    {
        incident.Transition(IncidentStatus.Investigating, null, now);
        db.Comments.Add(incident.AddComment(note, actor, now));
        db.StatusHistory.Add(StatusHistory.Create(incident.Id, actor, IncidentStatus.Open,
            IncidentStatus.Investigating, note, now));
    }

    private static async Task<Exception?> SaveAsync(IncidentDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            return exception;
        }
    }
}
