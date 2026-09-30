namespace HALProcessRecord.Utilities;

public static class TimingLimits
{
    public static readonly TimeSpan MaximumTransferDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumTotalDuration = TimeSpan.FromSeconds(210);
}
