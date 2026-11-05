namespace HALProcessRecord.Utilities;

public static class IndiaTime
{
    private static readonly TimeZoneInfo TimeZone = OperatingSystem.IsWindows()
        ? TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")
        : TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    public static DateTime Now => FromUtc(DateTime.UtcNow);

    public static DateTime FromUtc(DateTime utcTimestamp)
    {
        var utc = DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZone), DateTimeKind.Unspecified);
    }

    public static DateTime NormalizeOpcSourceTimestamp(DateTime sourceTimestamp, DateTime receivedAt)
    {
        var sourceUtc = DateTime.SpecifyKind(sourceTimestamp, DateTimeKind.Utc);
        if (sourceTimestamp == DateTime.MinValue ||
            sourceUtc < new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) ||
            sourceUtc > DateTime.UtcNow.AddMinutes(5))
        {
            return AsWallClock(receivedAt);
        }

        var normalizedSourceTimestamp = FromUtc(sourceUtc);
        return normalizedSourceTimestamp > AsWallClock(receivedAt).AddSeconds(10)
            ? AsWallClock(receivedAt)
            : normalizedSourceTimestamp;
    }

    public static DateTime AsWallClock(DateTime timestamp) =>
        DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified);
}
