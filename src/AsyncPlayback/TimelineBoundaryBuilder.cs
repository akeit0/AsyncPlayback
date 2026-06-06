namespace AsyncPlayback;

public sealed class TimelineBoundaryBuilder
{
    private readonly List<TimelineBoundary> boundaries;

    internal TimelineBoundaryBuilder(
        Playback playback,
        TimelineBoundaryScope scope,
        HashSet<TimeSpan>? timedBoundaryTimes,
        List<TimelineBoundary> boundaries
    )
    {
        Playback = playback;
        Scope = scope;
        TimedBoundaryTimes = timedBoundaryTimes;
        this.boundaries = boundaries;
    }

    internal Playback Playback { get; }
    internal TimelineBoundaryScope Scope { get; }
    internal HashSet<TimeSpan>? TimedBoundaryTimes { get; }

    public void AddPoint(in TimelineRecord record)
    {
        boundaries.Add(TimelineBoundary.Create(record, TimelineBoundaryKind.Point));
    }

    public void AddStart(in TimelineRecord record)
    {
        boundaries.Add(TimelineBoundary.Create(record, TimelineBoundaryKind.Start));
    }

    public void AddEnd(in TimelineRecord record)
    {
        if (record.Duration > TimeSpan.Zero)
            boundaries.Add(TimelineBoundary.Create(record, TimelineBoundaryKind.End));
    }
}
