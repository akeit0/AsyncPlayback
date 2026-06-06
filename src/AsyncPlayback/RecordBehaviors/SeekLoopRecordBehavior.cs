namespace AsyncPlayback;

internal sealed class SeekLoopRecordBehavior : TimelineRecordBehavior
{
    public SeekLoopRecordBehavior()
        : base(TimelineRecordTypes.SeekLoop.Id, TimelineRecordTypes.SeekLoop.Name) { }

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
        return record.Contains(query.Time, query.IncludeEnd);
    }

    public override ValueTask EvaluateAsync(
        RecordEvaluationContext context,
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        return context.Playback.EmitSeekLoopAtAsync(record, time, direction);
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

    internal override bool IsSeekLoopStartBoundary(
        in TimelineRecord record,
        TimelineBoundaryKind boundaryKind
    )
    {
        return boundaryKind == TimelineBoundaryKind.Start;
    }

    internal override bool ShouldEmitTimedRecordStart(in TimelineRecord record)
    {
        return true;
    }

    internal override bool IsSeekLoopRecord(in TimelineRecord record)
    {
        return true;
    }

    internal override bool HasSeekLoopEndingAt(in TimelineRecord record, TimeSpan time)
    {
        return record.EndTime == time;
    }
}
