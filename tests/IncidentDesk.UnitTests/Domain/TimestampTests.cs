using IncidentDesk.Domain;

namespace IncidentDesk.UnitTests.Domain;

public sealed class TimestampTests
{
    private static readonly Guid Reporter = Guid.Parse("a6674554-b0ea-4a3d-8a40-2008b58cbbbd");
    private static readonly Guid Engineer = Guid.Parse("5dd2a6e6-e065-4e3b-b922-6e6d2b696438");
    private static readonly DateTimeOffset Expected = new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero)
        .AddTicks(1_234_560);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void Creation_records_UTC_microseconds_for_incidents_comments_and_history(int remainingTicks)
    {
        var now = new DateTimeOffset(2026, 10, 6, 13, 30, 0, TimeSpan.FromMinutes(330))
            .AddTicks(1_234_560 + remainingTicks);
        var incident = Incident.Create("Queue stalled", "Messages are delayed.", Severity.High, Reporter, now);
        var comment = IncidentComment.Create(incident.Id, Engineer, "Investigating the worker.", now);
        var history = StatusHistory.Create(incident.Id, Reporter, null, IncidentStatus.Open, null, now);

        Assert.Equal(Expected, incident.CreatedAt);
        Assert.Equal(Expected, incident.UpdatedAt);
        Assert.Equal(Expected, comment.CreatedAt);
        Assert.Equal(Expected, history.CreatedAt);
        Assert.Equal(TimeSpan.Zero, incident.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, comment.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, history.CreatedAt.Offset);
    }

    [Fact]
    public void Mutations_record_UTC_microseconds_including_resolution_review_and_comments()
    {
        var incident = Incident.Create("Queue stalled", "Messages are delayed.", Severity.High, Reporter, Expected);
        var precise = Expected.ToOffset(TimeSpan.FromMinutes(330)).AddTicks(7);

        incident.UpdateDetails("Worker stalled", "Messages are delayed.", Severity.Critical, precise.AddSeconds(1));
        Assert.Equal(Expected.AddSeconds(1), incident.UpdatedAt);

        incident.Assign(Engineer, precise.AddSeconds(2));
        Assert.Equal(Expected.AddSeconds(2), incident.UpdatedAt);

        incident.Transition(IncidentStatus.Investigating, null, precise.AddSeconds(3));
        Assert.Equal(Expected.AddSeconds(3), incident.UpdatedAt);

        var comment = incident.AddComment("Restarted the worker.", Engineer, precise.AddSeconds(4));
        Assert.Equal(Expected.AddSeconds(4), incident.UpdatedAt);
        Assert.Equal(Expected.AddSeconds(4), comment.CreatedAt);

        incident.SaveSummary("The worker was restarted.", Engineer, precise.AddSeconds(5));
        Assert.Equal(Expected.AddSeconds(5), incident.UpdatedAt);
        Assert.Equal(Expected.AddSeconds(5), incident.SummaryReviewedAt);

        incident.Transition(IncidentStatus.Resolved, "Queue processing recovered.", precise.AddSeconds(6));
        Assert.Equal(Expected.AddSeconds(6), incident.UpdatedAt);
        Assert.Equal(Expected.AddSeconds(6), incident.ResolvedAt);
        Assert.Equal(Expected, incident.CreatedAt);
    }
}
