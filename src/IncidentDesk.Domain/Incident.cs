namespace IncidentDesk.Domain;

public sealed class Incident
{
    private Incident()
    {
    }

    public Guid Id { get; private set; }
    public Guid ReporterId { get; private set; }
    public Guid? AssigneeId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public Severity Severity { get; private set; }
    public IncidentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolutionNote { get; private set; }
    public string? Summary { get; private set; }
    public Guid? SummaryReviewedById { get; private set; }
    public DateTimeOffset? SummaryReviewedAt { get; private set; }
    public Guid Version { get; private set; }

    public static Incident Create(
        string title,
        string description,
        Severity severity,
        Guid reporterId,
        DateTimeOffset now)
    {
        var validTitle = DomainValidation.RequiredText(title, 200, "title");
        var validDescription = DomainValidation.RequiredText(description, 10_000, "description");
        DomainValidation.DefinedEnum(severity, "severity");
        DomainValidation.NonEmptyId(reporterId, "reporterId");

        return new Incident
        {
            Id = Guid.NewGuid(),
            ReporterId = reporterId,
            Title = validTitle,
            Description = validDescription,
            Severity = severity,
            Status = IncidentStatus.Open,
            CreatedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime(),
            Version = Guid.NewGuid()
        };
    }

    public void UpdateDetails(string title, string description, Severity severity, DateTimeOffset now)
    {
        EnsureActive();
        var validTitle = DomainValidation.RequiredText(title, 200, "title");
        var validDescription = DomainValidation.RequiredText(description, 10_000, "description");
        DomainValidation.DefinedEnum(severity, "severity");

        Title = validTitle;
        Description = validDescription;
        Severity = severity;
        Touch(now);
    }

    public void Assign(Guid? assigneeId, DateTimeOffset now)
    {
        EnsureActive();
        if (assigneeId is { } id)
        {
            DomainValidation.NonEmptyId(id, "assigneeId");
        }

        if (Status == IncidentStatus.Investigating && assigneeId is null)
        {
            throw new DomainException("assignment_required", "An investigating incident must remain assigned to an engineer.");
        }

        AssigneeId = assigneeId;
        Touch(now);
    }

    public void Transition(IncidentStatus target, string? resolutionNote, DateTimeOffset now)
    {
        DomainValidation.DefinedEnum(target, "status");
        EnsureActive();

        var isAllowed = (Status, target) is
            (IncidentStatus.Open, IncidentStatus.Investigating) or
            (IncidentStatus.Investigating, IncidentStatus.Resolved);

        if (!isAllowed)
        {
            throw new DomainException("invalid_transition", $"An incident cannot move from {Status} to {target}.");
        }

        if (target == IncidentStatus.Investigating && AssigneeId is null)
        {
            throw new DomainException("assignment_required", "Assign an engineer before starting an investigation.");
        }

        var validResolutionNote = target == IncidentStatus.Resolved
            ? DomainValidation.RequiredText(resolutionNote, 5_000, "resolutionNote")
            : null;

        Status = target;
        if (target == IncidentStatus.Resolved)
        {
            ResolutionNote = validResolutionNote;
            ResolvedAt = now.ToUniversalTime();
        }

        Touch(now);
    }

    public IncidentComment AddComment(string body, Guid authorId, DateTimeOffset now)
    {
        var comment = IncidentComment.Create(Id, authorId, body, now);
        Touch(now);
        return comment;
    }

    public void SaveSummary(string text, Guid reviewerId, DateTimeOffset now)
    {
        var validText = DomainValidation.RequiredText(text, 10_000, "summary");
        DomainValidation.NonEmptyId(reviewerId, "reviewerId");

        Summary = validText;
        SummaryReviewedById = reviewerId;
        SummaryReviewedAt = now.ToUniversalTime();
        Touch(now);
    }

    private void EnsureActive()
    {
        if (Status == IncidentStatus.Resolved)
        {
            throw new DomainException("incident_resolved", "Resolved incidents cannot change their details, assignment, or status.");
        }
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now.ToUniversalTime();
        Version = Guid.NewGuid();
    }
}
