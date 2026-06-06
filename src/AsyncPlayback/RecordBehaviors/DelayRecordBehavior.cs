namespace AsyncPlayback;

internal sealed class DelayRecordBehavior : TimelineRecordBehavior
{
    public DelayRecordBehavior()
        : base(TimelineRecordTypes.Delay.Id, TimelineRecordTypes.Delay.Name) { }

    public override void AddBoundaries(in TimelineRecord record, TimelineBoundaryBuilder builder)
    {
        builder.AddStart(record);
        builder.AddEnd(record);
    }

    public override bool IsReplayMatch(
        in TimelineRecord record,
        in TimelineRecordCreateRequest request
    )
    {
        return IsRequestedBehavior(record, request)
            && record.StartTime == request.StartTime
            && record.Duration == request.Duration
            && record.DebugLabel == request.DebugLabel;
    }

    public override bool IsEvaluatable(in TimelineRecord record, in RecordEvaluationQuery query)
    {
        if (record.EndTime == query.Time)
            return true;

        if (
            query.Direction != PlaybackDirection.Backward
            || record.Duration <= TimeSpan.Zero
            || query.Playback.TransportStartTime < record.EndTime
            || !query.Playback.HasSeekLoopEndingAt(record.StartTime, record.FlatIndex)
        )
            return false;

        return record.StartTime <= query.Time && query.Time < record.EndTime;
    }

    public override ValueTask EvaluateAsync(
        RecordEvaluationContext context,
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        return context.Playback.EmitDelayAtAsync(record, time, direction);
    }

    internal override TimelineBoundary? GetCurrentBoundaryPosition(
        in TimelineRecord record,
        TimeSpan time
    )
    {
        return GetTimedBoundaryPosition(record, time);
    }

    internal override bool HasTimedBoundaryAt(in TimelineRecord record, TimeSpan time)
    {
        return HasTimedBoundary(record, time);
    }

    internal override void AddTimedBoundaryTimes(
        in TimelineRecord record,
        HashSet<TimeSpan> boundaryTimes
    )
    {
        AddTimedBoundaries(record, boundaryTimes);
    }

    internal override bool IsDelayStartBoundary(
        in TimelineRecord record,
        TimelineBoundaryKind boundaryKind
    )
    {
        return boundaryKind == TimelineBoundaryKind.Start;
    }

    internal override bool IsDelayRecord(in TimelineRecord record)
    {
        return true;
    }

    internal override bool ShouldEmitTimedRecordStart(in TimelineRecord record)
    {
        return true;
    }

    internal override bool IsPendingBackwardDelayCandidate(
        in TimelineRecord record,
        TimeSpan currentTime,
        TimeSpan transportStartTime
    )
    {
        return record.Duration > TimeSpan.Zero
            && record.StartTime == currentTime
            && transportStartTime >= record.EndTime;
    }
}
