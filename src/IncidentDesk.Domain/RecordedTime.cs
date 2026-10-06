namespace IncidentDesk.Domain;

internal static class RecordedTime
{
    public static DateTimeOffset UtcMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        // PostgreSQL stores microseconds; keep write responses identical to later reads.
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    }
}
