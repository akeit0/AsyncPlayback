namespace AsyncPlayback;

public abstract class TimelineRecordBehavior : ITimelineRecordBehavior
{
    protected TimelineRecordBehavior(string typeId, string typeName)
    {
        TypeId = typeId;
        TypeName = typeName;
    }

    public string TypeId { get; }
    public string TypeName { get; }

    public virtual TimelineRecordVisibility GetVisibility(in TimelineRecord record)
    {
        return TimelineRecordVisibility.Workflow;
    }

    public virtual void AddBoundaries(in TimelineRecord record, TimelineBoundaryBuilder builder) { }

    public virtual bool IsReplayMatch(
        in TimelineRecord record,
        in TimelineRecordCreateRequest request
    )
    {
        return IsRequestedBehavior(record, request)
            && record.StartTime == request.StartTime
            && record.Duration == request.Duration
            && record.DebugLabel == request.DebugLabel;
    }

    public virtual bool IsEvaluatable(in TimelineRecord record, in RecordEvaluationQuery query)
    {
        return false;
    }

    public virtual ValueTask EvaluateAsync(
        Playback playback,
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        playback.MarkRecordEvaluated(record, null, time);
        return ValueTask.CompletedTask;
    }

    public virtual TimelineRecordInfo ToInfo(in TimelineRecord record)
    {
        return new(
            record.Id,
            TypeId,
            TypeName,
            record.StartTime,
            record.Duration,
            record.DebugLabel,
            record.ParentId,
            record.Depth,
            GetVisibility(record),
            null,
            record.EntryCheckpoint?.Timestamp,
            record.EntryCheckpoint?.DeltaTime
        );
    }

    internal virtual bool IsStepBoundaryIncluded(
        in TimelineRecord record,
        TimelineBoundary boundary,
        PlaybackStepGranularity granularity
    )
    {
        return granularity == PlaybackStepGranularity.AwaitPoint
            || GetVisibility(record) == TimelineRecordVisibility.Workflow;
    }

    internal virtual TimelineBoundary? GetCurrentBoundaryPosition(
        in TimelineRecord record,
        TimeSpan time
    )
    {
        return null;
    }

    internal virtual bool HasTimedBoundaryAt(in TimelineRecord record, TimeSpan time)
    {
        return false;
    }

    internal virtual void AddTimedBoundaryTimes(
        in TimelineRecord record,
        HashSet<TimeSpan> boundaryTimes
    ) { }

    internal virtual bool IsSeekLoopStartBoundary(
        in TimelineRecord record,
        TimelineBoundaryKind boundaryKind
    )
    {
        return false;
    }

    internal virtual bool IsDelayStartBoundary(
        in TimelineRecord record,
        TimelineBoundaryKind boundaryKind
    )
    {
        return false;
    }

    internal virtual bool ShouldEmitTimedRecordStart(in TimelineRecord record)
    {
        return false;
    }

    internal virtual bool IsCheckpointRecord(in TimelineRecord record)
    {
        return false;
    }

    internal virtual bool IsSeekLoopRecord(in TimelineRecord record)
    {
        return false;
    }

    internal virtual bool IsDelayRecord(in TimelineRecord record)
    {
        return false;
    }

    internal virtual bool IsPendingBackwardDelayCandidate(
        in TimelineRecord record,
        TimeSpan currentTime,
        TimeSpan transportStartTime
    )
    {
        return false;
    }

    internal virtual bool HasSeekLoopEndingAt(in TimelineRecord record, TimeSpan time)
    {
        return false;
    }

    internal virtual bool ShouldEmitCheckpointAdded(
        in TimelineRecord record,
        bool hadEntryCheckpoint
    )
    {
        return false;
    }

    internal virtual bool SuppressesCheckpointAutoContinuation(in TimelineRecord record)
    {
        return false;
    }

    internal virtual bool IsInitialPlaybackCheckpoint(
        in TimelineRecord record,
        IPlaybackRunner rootRunner
    )
    {
        return false;
    }

    protected static bool IsRequestedBehavior(
        in TimelineRecord record,
        in TimelineRecordCreateRequest request
    )
    {
        return record.Behavior.TypeId == request.Behavior.TypeId;
    }

    private protected static TimelineBoundary? GetTimedBoundaryPosition(
        in TimelineRecord record,
        TimeSpan time
    )
    {
        if (time == record.StartTime)
            return TimelineBoundary.Create(record, TimelineBoundaryKind.Start);

        if (record.Duration > TimeSpan.Zero && time == record.EndTime)
            return TimelineBoundary.Create(record, TimelineBoundaryKind.End);

        return null;
    }

    private protected static bool HasTimedBoundary(in TimelineRecord record, TimeSpan time)
    {
        return record.StartTime == time
            || (record.Duration > TimeSpan.Zero && record.EndTime == time);
    }

    private protected static void AddTimedBoundaries(
        in TimelineRecord record,
        HashSet<TimeSpan> boundaryTimes
    )
    {
        boundaryTimes.Add(record.StartTime);

        if (record.Duration > TimeSpan.Zero)
            boundaryTimes.Add(record.EndTime);
    }
}
