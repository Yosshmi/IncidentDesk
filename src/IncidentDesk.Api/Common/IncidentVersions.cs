namespace IncidentDesk.Api.Common;

public static class IncidentVersions
{
    public static string Format(Guid version) => $"\"{version:D}\"";

    public static void WriteHeaders(HttpResponse response, Guid version)
    {
        var value = Format(version);
        response.Headers.ETag = value;
        response.Headers["X-Incident-ETag"] = value;
        response.Headers.CacheControl = "no-store, no-transform";
    }

    public static void RequireMatch(HttpRequest request, Guid currentVersion)
    {
        if (!request.Headers.TryGetValue("If-Match", out var values))
        {
            throw new ApiException(428, "version_required", "Supply the incident ETag in the If-Match header.");
        }

        var value = values.Count == 1 ? values[0]?.Trim() : null;
        if (value is null || value.Length != 38 || value[0] != '"' || value[^1] != '"'
            || !Guid.TryParseExact(value[1..^1], "D", out var version))
        {
            throw new ApiException(400, "invalid_version", "If-Match must contain one quoted incident version, exactly as returned in ETag.");
        }

        if (version != currentVersion)
        {
            throw new ApiException(412, "version_conflict", "This incident changed. Fetch its latest version and review your changes before retrying.");
        }
    }
}
