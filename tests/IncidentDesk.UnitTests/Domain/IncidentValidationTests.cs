using IncidentDesk.Domain;

namespace IncidentDesk.UnitTests.Domain;

public sealed class IncidentValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 13, 30, 0, TimeSpan.FromHours(5.5));
    private static readonly Guid UserId = Guid.Parse("7b003b72-62f7-4e26-beed-199435ecb279");

    [Theory]
    [InlineData(Severity.Low)]
    [InlineData(Severity.Medium)]
    [InlineData(Severity.High)]
    [InlineData(Severity.Critical)]
    public void Creation_initializes_a_valid_open_incident(Severity severity)
    {
        var incident = Incident.Create("  Checkout delays  ", "  Payments time out.  ", severity, UserId, Now);

        Assert.NotEqual(Guid.Empty, incident.Id);
        Assert.NotEqual(Guid.Empty, incident.Version);
        Assert.Equal(UserId, incident.ReporterId);
        Assert.Equal("Checkout delays", incident.Title);
        Assert.Equal("Payments time out.", incident.Description);
        Assert.Equal(severity, incident.Severity);
        Assert.Equal(IncidentStatus.Open, incident.Status);
        Assert.Equal(Now.ToUniversalTime(), incident.CreatedAt);
        Assert.Equal(TimeSpan.Zero, incident.CreatedAt.Offset);
        Assert.Equal(incident.CreatedAt, incident.UpdatedAt);
        Assert.Null(incident.AssigneeId);
        Assert.Null(incident.ResolvedAt);
        Assert.Null(incident.ResolutionNote);
        Assert.Null(incident.Summary);
        Assert.Null(incident.SummaryReviewedById);
        Assert.Null(incident.SummaryReviewedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void Creation_rejects_blank_title_and_description(string? input)
    {
        AssertField("title", () => Incident.Create(input!, "Description", Severity.High, UserId, Now));
        AssertField("description", () => Incident.Create("Title", input!, Severity.High, UserId, Now));
    }

    [Fact]
    public void Creation_rejects_empty_reporter_and_unknown_severity()
    {
        AssertField("reporterId", () => Incident.Create("Title", "Description", Severity.High, Guid.Empty, Now));
        AssertField("severity", () => Incident.Create("Title", "Description", (Severity)(-1), UserId, Now));
    }

    [Theory]
    [InlineData("\0Incident")]
    [InlineData("Incident\0details")]
    [InlineData("Incident\0")]
    public void Creation_rejects_null_characters_in_text(string input)
    {
        AssertField("title", () => Incident.Create(input, "Description", Severity.High, UserId, Now));
        AssertField("description", () => Incident.Create("Title", input, Severity.High, UserId, Now));
    }

    [Theory]
    [InlineData("\0Incident")]
    [InlineData("Incident\0details")]
    [InlineData("Incident\0")]
    public void Null_characters_are_rejected_before_mutating_existing_incidents(string input)
    {
        var incident = NewIncident();
        incident.SaveSummary("Previously reviewed summary.", UserId, Now);
        var initialVersion = incident.Version;

        AssertField("title", () => incident.UpdateDetails(input, "Replacement description", Severity.Low, Now.AddMinutes(1)));
        AssertField("description", () => incident.UpdateDetails("Replacement title", input, Severity.Low, Now.AddMinutes(1)));
        AssertField("body", () => incident.AddComment(input, UserId, Now.AddMinutes(1)));
        AssertField("summary", () => incident.SaveSummary(input, Guid.NewGuid(), Now.AddMinutes(1)));

        Assert.Equal("Title", incident.Title);
        Assert.Equal("Description", incident.Description);
        Assert.Equal(Severity.High, incident.Severity);
        Assert.Equal("Previously reviewed summary.", incident.Summary);
        Assert.Equal(UserId, incident.SummaryReviewedById);
        Assert.Equal(Now, incident.SummaryReviewedAt);
        Assert.Equal(initialVersion, incident.Version);
        Assert.Equal(Now, incident.UpdatedAt);

        incident.Assign(UserId, Now);
        incident.Transition(IncidentStatus.Investigating, null, Now);
        var investigationVersion = incident.Version;

        AssertField("resolutionNote", () => incident.Transition(IncidentStatus.Resolved, input, Now.AddMinutes(1)));

        Assert.Equal(IncidentStatus.Investigating, incident.Status);
        Assert.Null(incident.ResolutionNote);
        Assert.Null(incident.ResolvedAt);
        Assert.Equal(investigationVersion, incident.Version);
        Assert.Equal(Now, incident.UpdatedAt);
        AssertField("note", () => StatusHistory.Create(incident.Id, UserId,
            IncidentStatus.Investigating, IncidentStatus.Resolved, input, Now));
    }

    [Fact]
    public void Text_limits_apply_after_trimming_and_allow_the_exact_limit()
    {
        var title = new string('t', 200);
        var description = new string('d', 10_000);
        var incident = Incident.Create($"  {title}  ", $"  {description}  ", Severity.High, UserId, Now);

        Assert.Equal(title, incident.Title);
        Assert.Equal(description, incident.Description);
        AssertField("title", () => Incident.Create(title + "x", description, Severity.High, UserId, Now));
        AssertField("description", () => Incident.Create(title, description + "x", Severity.High, UserId, Now));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    public void Oversized_detail_updates_preserve_existing_details_and_version(string field)
    {
        var incident = NewIncident();
        var oldVersion = incident.Version;

        AssertField(field, () => incident.UpdateDetails(
            field == "title" ? new string('t', 201) : "Changed title",
            field == "description" ? new string('d', 10_001) : "Changed description",
            Severity.Critical, Now.AddMinutes(1)));

        Assert.Equal("Title", incident.Title);
        Assert.Equal("Description", incident.Description);
        Assert.Equal(Severity.High, incident.Severity);
        Assert.Equal(oldVersion, incident.Version);
        Assert.Equal(Now, incident.UpdatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n")]
    public void Comments_and_summaries_reject_blank_text(string? input)
    {
        var incident = NewIncident();
        var oldVersion = incident.Version;

        AssertField("body", () => incident.AddComment(input!, UserId, Now.AddMinutes(1)));
        AssertField("summary", () => incident.SaveSummary(input!, UserId, Now.AddMinutes(1)));

        Assert.Equal(oldVersion, incident.Version);
        Assert.Null(incident.Summary);
        Assert.Null(incident.SummaryReviewedAt);
        Assert.Equal(Now, incident.UpdatedAt);
    }

    [Fact]
    public void Comment_length_limit_is_enforced_without_touching_the_incident_on_failure()
    {
        var incident = NewIncident();
        var body = new string('c', 5_000);
        var comment = incident.AddComment(body, UserId, Now);
        var versionAfterComment = incident.Version;

        Assert.Equal(body, comment.Body);
        AssertField("body", () => incident.AddComment(body + "x", UserId, Now.AddMinutes(1)));
        Assert.Equal(versionAfterComment, incident.Version);
        Assert.Equal(Now, incident.UpdatedAt);
    }

    [Fact]
    public void Summary_length_limit_is_enforced_without_replacing_the_previous_review()
    {
        var incident = NewIncident();
        var text = new string('s', 10_000);
        incident.SaveSummary(text, UserId, Now);
        var versionAfterReview = incident.Version;

        AssertField("summary", () => incident.SaveSummary(text + "x", Guid.NewGuid(), Now.AddMinutes(1)));

        Assert.Equal(text, incident.Summary);
        Assert.Equal(UserId, incident.SummaryReviewedById);
        Assert.Equal(Now, incident.SummaryReviewedAt);
        Assert.Equal(versionAfterReview, incident.Version);
    }

    [Fact]
    public void All_recorded_timestamps_use_utc()
    {
        var incident = NewIncident();
        incident.Assign(UserId, Now);
        incident.Transition(IncidentStatus.Investigating, null, Now);
        incident.Transition(IncidentStatus.Resolved, "Restarted service.", Now);
        incident.SaveSummary("Service was restored.", UserId, Now);
        var comment = incident.AddComment("Monitoring confirms recovery.", UserId, Now);

        Assert.Equal(TimeSpan.Zero, incident.UpdatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, incident.ResolvedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, incident.SummaryReviewedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, comment.CreatedAt.Offset);
    }

    [Fact]
    public void Comment_factory_records_identity_and_rejects_empty_identifiers()
    {
        var incidentId = Guid.NewGuid();
        var comment = IncidentComment.Create(incidentId, UserId, "  An observation.  ", Now);

        Assert.NotEqual(Guid.Empty, comment.Id);
        Assert.Equal(incidentId, comment.IncidentId);
        Assert.Equal(UserId, comment.AuthorId);
        Assert.Equal("An observation.", comment.Body);
        Assert.Equal(Now.ToUniversalTime(), comment.CreatedAt);
        AssertField("incidentId", () => IncidentComment.Create(Guid.Empty, UserId, "Note", Now));
        AssertField("authorId", () => IncidentComment.Create(incidentId, Guid.Empty, "Note", Now));
        AssertField("id", () => new IncidentComment(Guid.Empty, incidentId, UserId, "Note", Now));
    }

    [Fact]
    public void History_supports_creation_and_records_transition_context()
    {
        var incidentId = Guid.NewGuid();
        var creation = StatusHistory.Create(incidentId, UserId, null, IncidentStatus.Open, null, Now);
        var resolution = StatusHistory.Create(incidentId, UserId,
            IncidentStatus.Investigating, IncidentStatus.Resolved, "  Increased pool capacity.  ", Now);

        Assert.NotEqual(Guid.Empty, creation.Id);
        Assert.NotEqual(creation.Id, resolution.Id);
        Assert.Null(creation.FromStatus);
        Assert.Equal(IncidentStatus.Open, creation.ToStatus);
        Assert.Null(creation.Note);
        Assert.Equal(incidentId, resolution.IncidentId);
        Assert.Equal(UserId, resolution.ActorId);
        Assert.Equal(IncidentStatus.Investigating, resolution.FromStatus);
        Assert.Equal(IncidentStatus.Resolved, resolution.ToStatus);
        Assert.Equal("Increased pool capacity.", resolution.Note);
        Assert.Equal(Now.ToUniversalTime(), resolution.CreatedAt);
        Assert.Equal(TimeSpan.Zero, resolution.CreatedAt.Offset);
    }

    [Fact]
    public void History_rejects_invalid_identifiers_enums_and_notes()
    {
        var incidentId = Guid.NewGuid();
        AssertField("id", () => new StatusHistory(Guid.Empty, incidentId, UserId, null, IncidentStatus.Open, null, Now));
        AssertField("incidentId", () => StatusHistory.Create(Guid.Empty, UserId, null, IncidentStatus.Open, null, Now));
        AssertField("actorId", () => StatusHistory.Create(incidentId, Guid.Empty, null, IncidentStatus.Open, null, Now));
        AssertField("fromStatus", () => StatusHistory.Create(incidentId, UserId, (IncidentStatus)99, IncidentStatus.Open, null, Now));
        AssertField("toStatus", () => StatusHistory.Create(incidentId, UserId, null, (IncidentStatus)99, null, Now));
        AssertField("note", () => StatusHistory.Create(incidentId, UserId, null, IncidentStatus.Open, " ", Now));
        AssertField("note", () => StatusHistory.Create(incidentId, UserId, null, IncidentStatus.Open, new string('x', 5_001), Now));
    }

    private static Incident NewIncident()
        => Incident.Create("Title", "Description", Severity.High, UserId, Now);

    private static void AssertField(string field, Action action)
    {
        var exception = Assert.Throws<DomainException>(action);
        Assert.Equal("invalid_input", exception.Code);
        Assert.Equal(field, exception.Field);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }
}
