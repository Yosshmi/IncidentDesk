using System.Net;
using IncidentDesk.Api.Incidents;
using IncidentDesk.Api.Persistence;
using IncidentDesk.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class SearchTests(ApiFixture fixture)
{
    [Fact]
    public async Task Search_is_case_insensitive_and_treats_SQL_wildcards_literally()
    {
        var marker = $"Literal{Guid.NewGuid():N}";
        foreach (var (literal, decoy) in new[] { ("percent%", "percentXYZ"), ("under_score", "underXscore"), ("path\\segment", "pathXsegment") })
        {
            var actual = await fixture.CreateAsync(title: marker + " " + literal);
            await fixture.CreateAsync(title: marker + " " + decoy);
            var query = Uri.EscapeDataString((marker + " " + literal).ToUpperInvariant());
            var result = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
                await fixture.Engineer.GetAsync($"/api/v1/incidents?q={query}", TestContext.Current.CancellationToken), HttpStatusCode.OK);
            Assert.Equal(1, result.TotalCount);
            Assert.Equal(actual.Value.Id, Assert.Single(result.Items).Id);
        }
        var descriptionOnly = await fixture.CreateAsync(description: marker + " description match");
        var inDescription = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
            await fixture.Engineer.GetAsync($"/api/v1/incidents?q={marker}%20description", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(descriptionOnly.Value.Id, Assert.Single(inDescription.Items).Id);
    }

    [Fact]
    public async Task Filters_combine_with_ownership_and_inclusive_dates()
    {
        var marker = $"Filter{Guid.NewGuid():N}";
        var selected = await fixture.CreateAsync(title: marker + " critical", severity: "Critical");
        await fixture.CreateAsync(fixture.OtherReporter, marker + " low", "Low");
        selected = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Put, selected, "/assignment",
            new { assigneeId = DemoSeeder.EngineerOneId });
        selected = await ApiFixture.MutateAsync(fixture.Engineer, HttpMethod.Post, selected, "/transitions",
            new { status = "Investigating" });
        var timestamp = Uri.EscapeDataString(selected.Value.CreatedAt.ToString("O"));
        var url = $"/api/v1/incidents?q={marker}&status=Investigating&severity=Critical" +
            $"&reporterId={DemoSeeder.ReporterOneId}&assigneeId={DemoSeeder.EngineerOneId}" +
            $"&createdFrom={timestamp}&createdTo={timestamp}";
        var filtered = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
            await fixture.Engineer.GetAsync(url, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(selected.Value.Id, Assert.Single(filtered.Items).Id);
        var unassigned = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
            await fixture.Engineer.GetAsync($"/api/v1/incidents?q={marker}&unassigned=true", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(DemoSeeder.ReporterTwoId, Assert.Single(unassigned.Items).ReporterId);
        var assigned = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
            await fixture.Engineer.GetAsync($"/api/v1/incidents?q={marker}&unassigned=false", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(selected.Value.Id, Assert.Single(assigned.Items).Id);
        var outsideDate = Uri.EscapeDataString(selected.Value.CreatedAt.AddDays(1).ToString("O"));
        var future = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
            await fixture.Engineer.GetAsync($"/api/v1/incidents?q={marker}&createdFrom={outsideDate}", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Empty(future.Items);
    }

    [Fact]
    public async Task Pagination_is_stable_when_creation_times_tie_and_reports_total_count()
    {
        var marker = $"Paging{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var incidents = Enumerable.Range(0, 5).Select(number => Incident.Create(
            $"{marker} {number}", "Pagination fixture", Severity.Medium, DemoSeeder.ReporterOneId, now)).ToArray();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IncidentDbContext>();
            db.Incidents.AddRange(incidents);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var returned = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
                await fixture.Reporter.GetAsync($"/api/v1/incidents?q={marker}&pageSize=2&page={page}", TestContext.Current.CancellationToken), HttpStatusCode.OK);
            Assert.Equal(5, result.TotalCount);
            Assert.Equal(page, result.Page);
            Assert.Equal(2, result.PageSize);
            returned.AddRange(result.Items.Select(incident => incident.Id));
        }
        Assert.Equal(incidents.OrderByDescending(incident => incident.Id).Select(incident => incident.Id), returned);
        var beyond = await ApiFixture.ReadAsync<PagedResult<IncidentResponse>>(
            await fixture.Reporter.GetAsync($"/api/v1/incidents?q={marker}&pageSize=2&page=4", TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(5, beyond.TotalCount);
        Assert.Empty(beyond.Items);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("status=Unexpected")]
    [InlineData("status=99")]
    [InlineData("severity=99")]
    [InlineData("q=invalid%00search")]
    [InlineData("createdFrom=2026-12-01&createdTo=2026-01-01")]
    [InlineData("reporterId=00000000-0000-0000-0000-000000000000")]
    [InlineData("assigneeId=33333333-3333-3333-3333-333333333333&unassigned=true")]
    public async Task Invalid_filters_return_field_validation_errors(string query)
    {
        var problem = await ApiFixture.ProblemAsync(await fixture.Engineer.GetAsync($"/api/v1/incidents?{query}", TestContext.Current.CancellationToken), 400);
        Assert.True(problem.TryGetProperty("errors", out _));
    }
}
