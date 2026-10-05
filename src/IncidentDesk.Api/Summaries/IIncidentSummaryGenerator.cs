namespace IncidentDesk.Api.Summaries;

public interface IIncidentSummaryGenerator
{
    Task<string> GenerateAsync(SummaryInput input, CancellationToken cancellationToken);
}
