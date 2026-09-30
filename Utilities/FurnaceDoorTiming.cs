using HALProcessRecord.Models;

namespace HALProcessRecord.Utilities;

public static class FurnaceDoorTiming
{
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMinutes(2);

    public static List<(DateTime OpenAt, DateTime CloseAt)> GetCompletedIntervals(
        IReadOnlyList<FurnaceDataEntity> samples)
    {
        var ordered = samples.OrderBy(x => x.CapturedAt).ToList();
        var intervals = new List<(DateTime OpenAt, DateTime CloseAt)>();
        FurnaceDataEntity? previous = null;
        DateTime? openAt = null;
        var halfDoor = false;

        foreach (var sample in ordered)
        {
            if (sample.DoorOpen)
            {
                halfDoor = false;
            }

            if (sample.HalfDoor)
                halfDoor = true;

            if (!sample.DoorClosed)
            {
                openAt = EventTime(sample.DoorClosedSourceTimestamp, sample.CapturedAt);
            }

            var closeTransition = previous is { } previousSample &&
                                  !previousSample.DoorClosed &&
                                  sample.DoorClosed;
            if (closeTransition)
            {
                if (openAt is DateTime candidateOpenAt && !halfDoor)
                {
                    var closeAt = EventTime(sample.DoorClosedSourceTimestamp, sample.CapturedAt);
                    var sampleGap = sample.CapturedAt - previous!.CapturedAt;
                    if (closeAt > candidateOpenAt &&
                        sampleGap <= MaximumSampleGap &&
                        closeAt - candidateOpenAt <= TimingLimits.MaximumTotalDuration)
                        intervals.Add((candidateOpenAt, closeAt));

                    openAt = null;
                }
                else if (openAt is not null)
                {
                    openAt = null;
                }
            }

            previous = sample;
        }

        return intervals;
    }

    public static DateTime EventTime(DateTime? sourceTimestamp, DateTime capturedAt)
    {
        if (sourceTimestamp is not DateTime source || source.Year < 2020 ||
            source > capturedAt.AddSeconds(10) ||
            source < capturedAt.AddSeconds(-10) ||
            Math.Abs((capturedAt - source).TotalDays) > 30)
        {
            return IndiaTime.AsWallClock(capturedAt);
        }

        return IndiaTime.AsWallClock(source);
    }
}
