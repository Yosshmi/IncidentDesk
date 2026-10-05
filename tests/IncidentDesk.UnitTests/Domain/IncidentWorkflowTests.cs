using IncidentDesk.Domain;

namespace IncidentDesk.UnitTests.Domain;

public sealed class IncidentWorkflowTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Changed = Created.AddHours(1);
    private static readonly Guid Reporter = Guid.Parse("a6674554-b0ea-4a3d-8a40-2008b58cbbbd");
    private static readonly Guid Engineer = Guid.Parse("5dd2a6e6-e065-4e3b-b922-6e6d2b696438");

    [Theory]
    [InlineData(IncidentStatus.Open, IncidentStatus.Open, false)]
    [InlineData(IncidentStatus.Open, IncidentStatus.Investigating, true)]
    [InlineData(IncidentStatus.Open, IncidentStatus.Resolved, false)]
    [InlineData(IncidentStatus.Investigating, IncidentStatus.Open, false)]
    [InlineData(IncidentStatus.Investigating, IncidentStatus.Investigating, false)]
    [InlineData(IncidentStatus.Investigating, IncidentStatus.Resolved, true)]
    [InlineData(IncidentStatus.Resolved, IncidentStatus.Open, false)]
    [InlineData(IncidentStatus.Resolved, IncidentStatus.Investigating, false)]
    [InlineData(IncidentStatus.Resolved, IncidentStatus.Resolved, false)]
    public void Workflow_allows_only_the_two_forward_transitions(
        IncidentStatus source,
        IncidentStatus target,
        bool allowed)
    {
        var incident = InState(source);
        var before = Snapshot(incident);

        if (allowed)
        {
            incident.Transition(target, "  Restored the database connection pool.  ", Changed);

            Assert.Equal(target, incident.Status);
            AssertTouched(incident, before.Version);
            Assert.Equal(Created, incident.CreatedAt);
            Assert.Equal(Engineer, incident.AssigneeId);
            if (target == IncidentStatus.Resolved)
            {
                Assert.Equal("Restored the database connection pool.", incident.ResolutionNote);
                Assert.Equal(Changed, incident.ResolvedAt);
            }
            else
            {
                Assert.Null(incident.ResolutionNote);
                Assert.Null(incident.ResolvedAt);
            }
        }
        else
        {
            var exception = Assert.Throws<DomainException>(
                () => incident.Transition(target, "Restored service.", Changed));

            Assert.Equal(source == IncidentStatus.Resolved ? "incident_resolved" : "invalid_transition", exception.Code);
            Assert.Equal(before, Snapshot(incident));
        }
    }

    [Fact]
    public void Investigation_requires_assignment_and_failure_preserves_the_incident()
    {
        var incident = NewIncident();
        var before = Snapshot(incident);

        var exception = Assert.Throws<DomainException>(
            () => incident.Transition(IncidentStatus.Investigating, null, Changed));

        Assert.Equal("assignment_required", exception.Code);
        Assert.Equal(before, Snapshot(incident));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void Resolution_requires_a_nonblank_note_without_partial_mutation(string? note)
    {
        var incident = InState(IncidentStatus.Investigating);
        var before = Snapshot(incident);

        var exception = Assert.Throws<DomainException>(
            () => incident.Transition(IncidentStatus.Resolved, note, Changed));

        AssertInvalidInput(exception, "resolutionNote");
        Assert.Equal(before, Snapshot(incident));
    }

    [Fact]
    public void Resolution_rejects_an_oversized_note_without_partial_mutation()
    {
        var incident = InState(IncidentStatus.Investigating);
        var before = Snapshot(incident);

        var exception = Assert.Throws<DomainException>(
            () => incident.Transition(IncidentStatus.Resolved, new string('x', 5_001), Changed));

        AssertInvalidInput(exception, "resolutionNote");
        Assert.Equal(before, Snapshot(incident));
    }

    [Fact]
    public void Resolution_accepts_the_maximum_note_length()
    {
        var incident = InState(IncidentStatus.Investigating);
        var note = new string('x', 5_000);

        incident.Transition(IncidentStatus.Resolved, note, Changed);

        Assert.Equal(note, incident.ResolutionNote);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
    }

    [Fact]
    public void Unknown_status_is_invalid_input_and_does_not_mutate()
    {
        var incident = InState(IncidentStatus.Open);
        var before = Snapshot(incident);

        var exception = Assert.Throws<DomainException>(
            () => incident.Transition((IncidentStatus)99, null, Changed));

        AssertInvalidInput(exception, "status");
        Assert.Equal(before, Snapshot(incident));
    }

    [Fact]
    public void Open_incidents_can_be_assigned_and_unassigned()
    {
        var incident = NewIncident();
        var originalVersion = incident.Version;

        incident.Assign(Engineer, Changed);

        Assert.Equal(Engineer, incident.AssigneeId);
        AssertTouched(incident, originalVersion);
        var assignedVersion = incident.Version;

        incident.Assign(null, Changed);

        Assert.Null(incident.AssigneeId);
        AssertTouched(incident, assignedVersion);
    }

    [Fact]
    public void Investigating_incidents_can_be_reassigned_but_not_unassigned()
    {
        var incident = InState(IncidentStatus.Investigating);
        var secondEngineer = Guid.NewGuid();
        var originalVersion = incident.Version;

        incident.Assign(secondEngineer, Changed);

        Assert.Equal(secondEngineer, incident.AssigneeId);
        AssertTouched(incident, originalVersion);
        var beforeUnassign = Snapshot(incident);

        var exception = Assert.Throws<DomainException>(() => incident.Assign(null, Changed.AddMinutes(1)));

        Assert.Equal("assignment_required", exception.Code);
        Assert.Equal(beforeUnassign, Snapshot(incident));
    }

    [Fact]
    public void Empty_assignee_id_is_invalid_and_does_not_mutate()
    {
        var incident = NewIncident();
        var before = Snapshot(incident);

        var exception = Assert.Throws<DomainException>(() => incident.Assign(Guid.Empty, Changed));

        AssertInvalidInput(exception, "assigneeId");
        Assert.Equal(before, Snapshot(incident));
    }

    [Theory]
    [InlineData(IncidentStatus.Open)]
    [InlineData(IncidentStatus.Investigating)]
    public void Active_incident_details_can_be_updated(IncidentStatus status)
    {
        var incident = InState(status);
        var originalVersion = incident.Version;

        incident.UpdateDetails("  Increased error rate  ", "  Investigating checkout timeouts.  ", Severity.Critical, Changed);

        Assert.Equal("Increased error rate", incident.Title);
        Assert.Equal("Investigating checkout timeouts.", incident.Description);
        Assert.Equal(Severity.Critical, incident.Severity);
        Assert.Equal(status, incident.Status);
        Assert.Equal(Reporter, incident.ReporterId);
        Assert.Equal(Created, incident.CreatedAt);
        AssertTouched(incident, originalVersion);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("severity")]
    public void Detail_validation_is_atomic(string invalidField)
    {
        var incident = NewIncident();
        var before = Snapshot(incident);

        var exception = Assert.Throws<DomainException>(() => incident.UpdateDetails(
            invalidField == "title" ? " " : "Valid replacement title",
            invalidField == "description" ? " " : "Valid replacement description",
            invalidField == "severity" ? (Severity)99 : Severity.Critical,
            Changed));

        AssertInvalidInput(exception, invalidField);
        Assert.Equal(before, Snapshot(incident));
    }

    [Theory]
    [InlineData("details")]
    [InlineData("assignment")]
    [InlineData("unassignment")]
    public void Resolved_incidents_freeze_core_fields_and_assignment(string operation)
    {
        var incident = InState(IncidentStatus.Resolved);
        var before = Snapshot(incident);

        var exception = Assert.Throws<DomainException>(() =>
        {
            switch (operation)
            {
                case "details":
                    incident.UpdateDetails("Changed", "Changed description", Severity.Low, Changed);
                    break;
                case "assignment":
                    incident.Assign(Guid.NewGuid(), Changed);
                    break;
                case "unassignment":
                    incident.Assign(null, Changed);
                    break;
            }
        });

        Assert.Equal("incident_resolved", exception.Code);
        Assert.Equal(before, Snapshot(incident));
    }

    [Theory]
    [InlineData(IncidentStatus.Open)]
    [InlineData(IncidentStatus.Investigating)]
    [InlineData(IncidentStatus.Resolved)]
    public void Comments_can_be_appended_in_every_state_and_change_the_aggregate_version(IncidentStatus status)
    {
        var incident = InState(status);
        var originalVersion = incident.Version;
        var originalResolution = incident.ResolutionNote;
        var originalResolvedAt = incident.ResolvedAt;

        var comment = incident.AddComment("  Checked database pool metrics.  ", Engineer, Changed);

        Assert.NotEqual(Guid.Empty, comment.Id);
        Assert.Equal(incident.Id, comment.IncidentId);
        Assert.Equal(Engineer, comment.AuthorId);
        Assert.Equal("Checked database pool metrics.", comment.Body);
        Assert.Equal(Changed, comment.CreatedAt);
        Assert.Equal(status, incident.Status);
        Assert.Equal(originalResolution, incident.ResolutionNote);
        Assert.Equal(originalResolvedAt, incident.ResolvedAt);
        AssertTouched(incident, originalVersion);
    }

    [Theory]
    [InlineData(IncidentStatus.Open)]
    [InlineData(IncidentStatus.Investigating)]
    [InlineData(IncidentStatus.Resolved)]
    public void Reviewed_summaries_can_be_saved_in_every_state(IncidentStatus status)
    {
        var incident = InState(status);
        var originalVersion = incident.Version;
        var originalResolution = incident.ResolutionNote;
        var originalResolvedAt = incident.ResolvedAt;

        incident.SaveSummary("  Connection exhaustion caused checkout timeouts.  ", Engineer, Changed);

        Assert.Equal("Connection exhaustion caused checkout timeouts.", incident.Summary);
        Assert.Equal(Engineer, incident.SummaryReviewedById);
        Assert.Equal(Changed, incident.SummaryReviewedAt);
        Assert.Equal(status, incident.Status);
        Assert.Equal(originalResolution, incident.ResolutionNote);
        Assert.Equal(originalResolvedAt, incident.ResolvedAt);
        AssertTouched(incident, originalVersion);
    }

    [Fact]
    public void Subsequent_review_replaces_summary_and_reviewer_metadata()
    {
        var incident = InState(IncidentStatus.Resolved);
        incident.SaveSummary("Initial review.", Engineer, Created.AddMinutes(10));
        var oldVersion = incident.Version;
        var nextReviewer = Guid.NewGuid();

        incident.SaveSummary("Corrected review.", nextReviewer, Changed);

        Assert.Equal("Corrected review.", incident.Summary);
        Assert.Equal(nextReviewer, incident.SummaryReviewedById);
        Assert.Equal(Changed, incident.SummaryReviewedAt);
        AssertTouched(incident, oldVersion);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("authorId")]
    [InlineData("summary")]
    [InlineData("reviewerId")]
    public void Invalid_comment_or_summary_does_not_change_any_incident_state(string invalidField)
    {
        var incident = InState(IncidentStatus.Resolved);
        incident.SaveSummary("A previously reviewed summary.", Engineer, Created.AddMinutes(5));
        var before = Snapshot(incident);

        var exception = Assert.Throws<DomainException>(() =>
        {
            if (invalidField is "body" or "authorId")
            {
                incident.AddComment(invalidField == "body" ? " " : "A valid note.",
                    invalidField == "authorId" ? Guid.Empty : Engineer, Changed);
            }
            else
            {
                incident.SaveSummary(invalidField == "summary" ? " " : "A valid new summary.",
                    invalidField == "reviewerId" ? Guid.Empty : Engineer, Changed);
            }
        });

        AssertInvalidInput(exception, invalidField);
        Assert.Equal(before, Snapshot(incident));
    }

    [Fact]
    public void Versions_change_even_if_the_clock_has_not_advanced()
    {
        var incident = NewIncident();
        var versions = new HashSet<Guid> { incident.Version };

        incident.Assign(Engineer, Created);
        Assert.True(versions.Add(incident.Version));
        incident.UpdateDetails(incident.Title, incident.Description, incident.Severity, Created);
        Assert.True(versions.Add(incident.Version));
        incident.Transition(IncidentStatus.Investigating, null, Created);
        Assert.True(versions.Add(incident.Version));
        incident.AddComment("A new observation.", Engineer, Created);
        Assert.True(versions.Add(incident.Version));
        incident.SaveSummary("A reviewed summary.", Engineer, Created);
        Assert.True(versions.Add(incident.Version));
        incident.Transition(IncidentStatus.Resolved, "Restarted the affected worker.", Created);
        Assert.True(versions.Add(incident.Version));

        Assert.Equal(Created, incident.UpdatedAt);
        Assert.DoesNotContain(Guid.Empty, versions);
    }

    private static Incident NewIncident()
        => Incident.Create("Checkout unavailable", "Customers receive a timeout during checkout.", Severity.High, Reporter, Created);

    private static Incident InState(IncidentStatus status)
    {
        var incident = NewIncident();
        incident.Assign(Engineer, Created.AddMinutes(1));
        if (status is IncidentStatus.Investigating or IncidentStatus.Resolved)
        {
            incident.Transition(IncidentStatus.Investigating, null, Created.AddMinutes(2));
        }

        if (status == IncidentStatus.Resolved)
        {
            incident.Transition(IncidentStatus.Resolved, "Restarted the affected worker.", Created.AddMinutes(3));
        }

        return incident;
    }

    private static void AssertTouched(Incident incident, Guid oldVersion)
    {
        Assert.NotEqual(Guid.Empty, incident.Version);
        Assert.NotEqual(oldVersion, incident.Version);
        Assert.Equal(Changed, incident.UpdatedAt);
    }

    private static void AssertInvalidInput(DomainException exception, string field)
    {
        Assert.Equal("invalid_input", exception.Code);
        Assert.Equal(field, exception.Field);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    private static IncidentSnapshot Snapshot(Incident incident)
        => new(incident.Id, incident.ReporterId, incident.AssigneeId, incident.Title, incident.Description,
            incident.Severity, incident.Status, incident.CreatedAt, incident.UpdatedAt, incident.ResolvedAt,
            incident.ResolutionNote, incident.Summary, incident.SummaryReviewedById, incident.SummaryReviewedAt,
            incident.Version);

    private sealed record IncidentSnapshot(
        Guid Id,
        Guid ReporterId,
        Guid? AssigneeId,
        string Title,
        string Description,
        Severity Severity,
        IncidentStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ResolvedAt,
        string? ResolutionNote,
        string? Summary,
        Guid? SummaryReviewedById,
        DateTimeOffset? SummaryReviewedAt,
        Guid Version);
}
