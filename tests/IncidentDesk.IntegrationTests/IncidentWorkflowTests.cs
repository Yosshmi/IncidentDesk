using System.Net;
using System.Net.Http.Json;
using IncidentDesk.Api.Incidents;
using IncidentDesk.Api.Persistence;
using IncidentDesk.Domain;

namespace IncidentDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class IncidentWorkflowTests(ApiFixture fixture)
{
    [Fact]
    public async Task Full_workflow_persists_authors_history_and_reviewed_summary()
    {
        var incident = await fixture.CreateAsync();
        Assert.Equal(IncidentStatus.Open, incident.Value.Status);
        Assert.Equal(Severity.Medium, incident.Value.Severity);
        Assert.Equal(DemoSeeder.ReporterOneId, incident.Value.ReporterId);
        var path = $"/api/v1/incidents/{incident.Value.Id}";

        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Post,
            path + "/transitions", new { status = "Resolved", resolutionNote = "Cannot skip investigation." }, incident.ETag),
            409, "invalid_transition");
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Post,
            path + "/transitions", new { status = "Investigating" }, incident.ETag), 409, "assignment_required");

        incident = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Put, incident, "/assignment",
            new { assigneeId = DemoSeeder.EngineerOneId });
        incident = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Post, incident, "/transitions",
            new { status = "Investigating" });
        Assert.Equal(IncidentStatus.Investigating, incident.Value.Status);

        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Put,
            path + "/assignment", new { assigneeId = (Guid?)null }, incident.ETag), 409, "assignment_required");
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Post,
            path + "/transitions", new { status = "Open" }, incident.ETag), 409, "invalid_transition");
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Post,
            path + "/transitions", new { status = "Resolved", resolutionNote = "  " }, incident.ETag), 400, "invalid_input");

        var noteResponse = await ApiFixture.SendAsync(fixture.Reporter, HttpMethod.Post, path + "/comments",
            new { body = "Reporter confirmed the delay in the sandbox." }, incident.ETag);
        var note = await ApiFixture.ReadAsync<CommentResponse>(noteResponse, HttpStatusCode.Created);
        Assert.Equal(DemoSeeder.ReporterOneId, note.AuthorId);
        Assert.Equal(incident.Value.Id, note.IncidentId);
        incident = incident with { ETag = ApiFixture.RequiredETag(noteResponse) };

        const string resolution = "Fixed the queue consumer and verified end-to-end processing.";
        incident = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Post, incident, "/transitions",
            new { status = "Resolved", resolutionNote = resolution });
        Assert.Equal(IncidentStatus.Resolved, incident.Value.Status);
        Assert.Equal(resolution, incident.Value.ResolutionNote);
        Assert.NotNull(incident.Value.ResolvedAt);

        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Put, path,
            new { title = "Changed", description = "Changed", severity = "High" }, incident.ETag), 409, "incident_resolved");
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Put, path + "/assignment",
            new { assigneeId = DemoSeeder.EngineerTwoId }, incident.ETag), 409, "incident_resolved");
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Post, path + "/transitions",
            new { status = "Investigating" }, incident.ETag), 409, "incident_resolved");

        var followUp = await ApiFixture.SendAsync(fixture.OtherEngineer, HttpMethod.Post, path + "/comments",
            new { body = "Follow-up verification completed after resolution." }, incident.ETag);
        await ApiFixture.ReadAsync<CommentResponse>(followUp, HttpStatusCode.Created);
        incident = incident with { ETag = ApiFixture.RequiredETag(followUp) };
        incident = await ApiFixture.MutateAsync(fixture.OtherEngineer, HttpMethod.Put, incident, "/summary",
            new { text = "The engineer fixed queue processing and verified the result." });
        Assert.Equal(DemoSeeder.EngineerTwoId, incident.Value.SummaryReviewedById);
        Assert.NotNull(incident.Value.SummaryReviewedAt);

        var persisted = await ApiFixture.ReadAsync<IncidentResponse>(await fixture.Reporter.GetAsync(path, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(incident.Value, persisted);
        var history = await ApiFixture.ReadAsync<PagedResult<HistoryResponse>>(
            await fixture.Reporter.GetAsync(path + "/history", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(3, history.TotalCount);
        Assert.Equal([IncidentStatus.Open, IncidentStatus.Investigating, IncidentStatus.Resolved],
            history.Items.Select(item => item.ToStatus));
        Assert.Null(history.Items[0].FromStatus);
        Assert.Equal(DemoSeeder.ReporterOneId, history.Items[0].ActorId);
        Assert.Equal(DemoSeeder.EngineerOneId, history.Items[2].ActorId);
        Assert.Equal(resolution, history.Items[2].Note);

        var comments = await ApiFixture.ReadAsync<PagedResult<CommentResponse>>(
            await fixture.Reporter.GetAsync(path + "/comments?pageSize=1&page=2", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(2, comments.TotalCount);
        Assert.Single(comments.Items);
        Assert.Equal(DemoSeeder.EngineerTwoId, comments.Items[0].AuthorId);
        var secondHistoryPage = await ApiFixture.ReadAsync<PagedResult<HistoryResponse>>(
            await fixture.Engineer.GetAsync(path + "/history?pageSize=2&page=2", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(3, secondHistoryPage.TotalCount);
        Assert.Equal(IncidentStatus.Resolved, Assert.Single(secondHistoryPage.Items).ToStatus);
    }

    [Fact]
    public async Task Ownership_applies_to_results_counts_notes_history_and_mutations()
    {
        var marker = $"Ownership{Guid.NewGuid():N}";
        var own = await fixture.CreateAsync(fixture.Reporter, marker + " own");
        var other = await fixture.CreateAsync(fixture.OtherReporter, marker + " other");
        var mine = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
            await fixture.Reporter.GetAsync($"/api/v1/incidents?q={marker}", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(1, mine.TotalCount);
        Assert.Equal(own.Value.Id, Assert.Single(mine.Items).Id);
        var all = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
            await fixture.Engineer.GetAsync($"/api/v1/incidents?q={marker}", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(2, all.TotalCount);
        var filtered = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
            await fixture.Reporter.GetAsync($"/api/v1/incidents?q={marker}&reporterId={DemoSeeder.ReporterTwoId}", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(0, filtered.TotalCount);

        var otherPath = $"/api/v1/incidents/{other.Value.Id}";
        foreach (var suffix in new[] { "", "/comments", "/history" })
        {
            await ApiFixture.ProblemAsync(await fixture.Reporter.GetAsync(otherPath + suffix, TestContext.Current.CancellationToken), 404, "incident_not_found");
        }
        await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Reporter, HttpMethod.Post,
            otherPath + "/comments", new { body = "Forbidden" }, other.ETag), 404, "incident_not_found");
        foreach (var (method, suffix, payload) in new (HttpMethod, string, object)[]
        {
            (HttpMethod.Put, "", new { title = "Forbidden", description = "Forbidden", severity = "Low" }),
            (HttpMethod.Put, "/assignment", new { assigneeId = DemoSeeder.EngineerOneId }),
            (HttpMethod.Post, "/transitions", new { status = "Investigating" }),
            (HttpMethod.Put, "/summary", new { text = "Forbidden" })
        })
        {
            await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Reporter, method,
                $"/api/v1/incidents/{own.Value.Id}{suffix}", payload, own.ETag), 403);
        }
    }

    [Fact]
    public async Task Assignment_accepts_only_existing_engineers_and_can_clear_an_open_incident()
    {
        var incident = await fixture.CreateAsync();
        foreach (var invalid in new[] { DemoSeeder.ReporterOneId, Guid.NewGuid(), Guid.Empty })
        {
            await ApiFixture.ProblemAsync(await ApiFixture.SendAsync(fixture.Engineer, HttpMethod.Put,
                $"/api/v1/incidents/{incident.Value.Id}/assignment", new { assigneeId = invalid }, incident.ETag),
                400, "invalid_assignee");
        }
        incident = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Put, incident, "/assignment",
            new { assigneeId = DemoSeeder.EngineerTwoId });
        Assert.Equal(DemoSeeder.EngineerTwoId, incident.Value.AssigneeId);
        incident = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Put, incident, "/assignment",
            new { assigneeId = (Guid?)null });
        Assert.Null(incident.Value.AssigneeId);
    }

    [Fact]
    public async Task Creation_uses_authenticated_actor_and_validates_input()
    {
        var forged = await fixture.Reporter.PostAsJsonAsync("/api/v1/incidents", new
        {
            title = "  Trimmed incident  ",
            description = "  Description  ",
            reporterId = DemoSeeder.ReporterTwoId,
            status = "Resolved"
        }, TestContext.Current.CancellationToken);
        await ApiFixture.ProblemAsync(forged, 400);
        var created = await fixture.Reporter.PostAsJsonAsync("/api/v1/incidents", new
        {
            title = "  Trimmed incident  ",
            description = "  Description  "
        }, TestContext.Current.CancellationToken);
        var incident = await ApiFixture.ReadAsync<IncidentResponse>(created, HttpStatusCode.Created);
        Assert.Equal("Trimmed incident", incident.Title);
        Assert.Equal("Description", incident.Description);
        Assert.Equal(DemoSeeder.ReporterOneId, incident.ReporterId);
        Assert.Equal(IncidentStatus.Open, incident.Status);
        Assert.Equal(Severity.Medium, incident.Severity);

        foreach (var body in new object[]
        {
            new { title = " ", description = "description", severity = "Medium" },
            new { title = new string('x', 201), description = "description", severity = "Medium" },
            new { title = "title", description = "", severity = "Medium" },
            new { title = "Invalid\0title", description = "description", severity = "Medium" },
            new { title = "title", description = new string('x', 10001), severity = "Medium" },
            new { title = "title", description = "description", severity = "Unexpected" },
            new { title = "title", description = "description", severity = 999 }
        })
        {
            var problem = await ApiFixture.ProblemAsync(await fixture.Reporter.PostAsJsonAsync("/api/v1/incidents", body, TestContext.Current.CancellationToken), 400);
            Assert.True(problem.TryGetProperty("errors", out _));
        }
    }
}
