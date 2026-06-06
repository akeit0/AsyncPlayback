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

    public override void AddEvaluationEntries(
        in TimelineRecord record,
        TimelineEvaluationBuilder builder
    )
    {
        builder.AddRange(record.StartTime, record.EndTime);
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
        return record.StartTime <= query.Time
            && (query.IncludeEnd ? query.Time <= record.EndTime : query.Time < record.EndTime);
    }

    public override ValueTask EvaluateAsync(
        Playback playback,
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        return EvaluateSeekLoopAsync(playback, record, time, direction);
    }

    private static async ValueTask EvaluateSeekLoopAsync(
        Playback playback,
        TimelineRecord loop,
        TimeSpan targetTime,
        PlaybackDirection direction
    )
    {
        var active = playback.HasActiveSeekLoop(loop);
        var mustRestore = direction == PlaybackDirection.Backward || !active;

        if (mustRestore)
            playback.RestoreToRecord(loop.Id);

        playback.MoveTimeTo(targetTime);

        if (direction == PlaybackDirection.Backward && targetTime == loop.EndTime)
            playback.SuppressLoopExitForRecord(loop.FlatIndex);

        playback.EmitSeekLoopTrueAt(loop, targetTime);
        await playback.RunReadyAsync(playback.CurrentCancellationToken).ConfigureAwait(false);

        playback.SetCurrentRecord(loop.Id);
        playback.EmitBoundaryReached(
            loop.Id,
            Playback.ToBoundaryKind(loop, targetTime),
            targetTime
        );
    }

    internal override TimelineBoundary? GetCurrentBoundaryPosition(
        in TimelineRecord record,
        TimeSpan time
    )
    {
        return GetTimedBoundaryPosition(record, time);
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
