namespace IncidentDesk.Api.Summaries;

public sealed record SummaryInput(
    string Title,
    string Description,
    string Status,
    string? ResolutionNote,
    IReadOnlyList<SummaryNote> Notes);

public sealed record SummaryNote(string Body, DateTimeOffset CreatedAt);
