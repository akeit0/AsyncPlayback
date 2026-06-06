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

    public override void AddEvaluationEntries(
        in TimelineRecord record,
        TimelineEvaluationBuilder builder
    )
    {
        builder.AddPoint(record.EndTime);

        if (record.Duration > TimeSpan.Zero)
            builder.AddRange(
                record.StartTime,
                record.EndTime,
                PlaybackDirection.Backward,
                includeEnd: false
            );
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
        Playback playback,
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        return EvaluateDelayAsync(playback, record, time, direction);
    }

    private static async ValueTask EvaluateDelayAsync(
        Playback playback,
        TimelineRecord delay,
        TimeSpan targetTime,
        PlaybackDirection direction
    )
    {
        var mustRestore =
            direction == PlaybackDirection.Backward || !playback.HasPendingDelay(delay.FlatIndex);

        if (mustRestore)
            playback.RestoreToRecord(delay.Id);

        playback.MoveTimeTo(targetTime);

        playback.CompleteDelayRecord(delay.FlatIndex);
        playback.SetCurrentRecord(delay.Id);
        playback.EmitBoundaryReached(
            delay.Id,
            Playback.ToBoundaryKind(delay, targetTime),
            targetTime
        );

        await playback.RunReadyAsync(playback.CurrentCancellationToken).ConfigureAwait(false);
        playback.EmitCurrentBoundaryReachedAt(targetTime);
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
