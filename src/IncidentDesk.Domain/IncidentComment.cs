namespace IncidentDesk.Domain;

public sealed class IncidentComment
{
    private IncidentComment()
    {
    }

    public IncidentComment(Guid id, Guid incidentId, Guid authorId, string body, DateTimeOffset createdAt)
    {
        DomainValidation.NonEmptyId(id, "id");
        DomainValidation.NonEmptyId(incidentId, "incidentId");
        DomainValidation.NonEmptyId(authorId, "authorId");
        var validBody = DomainValidation.RequiredText(body, 5_000, "body");

        Id = id;
        IncidentId = incidentId;
        AuthorId = authorId;
        Body = validBody;
        CreatedAt = RecordedTime.UtcMicroseconds(createdAt);
    }

    public Guid Id { get; private set; }
    public Guid IncidentId { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public static IncidentComment Create(Guid incidentId, Guid authorId, string body, DateTimeOffset now)
        => new(Guid.NewGuid(), incidentId, authorId, body, now);
}
