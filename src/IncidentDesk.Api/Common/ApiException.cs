namespace IncidentDesk.Api.Common;

public sealed class ApiException(int status, string code, string message, string? field = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? Field { get; } = field;
}
