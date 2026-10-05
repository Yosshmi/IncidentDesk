namespace IncidentDesk.Api.Summaries;

public sealed class OpenAiOptions
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-4.1-mini";
    public int TimeoutSeconds { get; set; } = 30;
}
