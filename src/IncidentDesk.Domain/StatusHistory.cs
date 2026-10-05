namespace IncidentDesk.Domain;

public sealed class StatusHistory
{
    private StatusHistory()
    {
    }

    public StatusHistory(
        Guid id,
        Guid incidentId,
        Guid actorId,
        IncidentStatus? fromStatus,
        IncidentStatus toStatus,
        string? note,
        DateTimeOffset createdAt)
    {
        DomainValidation.NonEmptyId(id, "id");
        DomainValidation.NonEmptyId(incidentId, "incidentId");
        DomainValidation.NonEmptyId(actorId, "actorId");
        if (fromStatus is { } previousStatus)
        {
            DomainValidation.DefinedEnum(previousStatus, "fromStatus");
        }

        DomainValidation.DefinedEnum(toStatus, "toStatus");
        var validNote = note is null ? null : DomainValidation.RequiredText(note, 5_000, "note");

        Id = id;
        IncidentId = incidentId;
        ActorId = actorId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Note = validNote;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid IncidentId { get; private set; }
    public Guid ActorId { get; private set; }
    public IncidentStatus? FromStatus { get; private set; }
    public IncidentStatus ToStatus { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static StatusHistory Create(
        Guid incidentId,
        Guid actorId,
        IncidentStatus? fromStatus,
        IncidentStatus toStatus,
        string? note,
        DateTimeOffset now)
        => new(Guid.NewGuid(), incidentId, actorId, fromStatus, toStatus, note, now);
}
