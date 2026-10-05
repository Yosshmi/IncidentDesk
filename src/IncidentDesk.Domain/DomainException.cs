namespace IncidentDesk.Domain;

public sealed class DomainException(string code, string message, string? field = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public string? Field { get; } = field;
}
