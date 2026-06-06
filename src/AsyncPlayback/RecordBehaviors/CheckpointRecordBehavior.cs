namespace AsyncPlayback;

internal sealed class CheckpointRecordBehavior : TimelineRecordBehavior
{
    public CheckpointRecordBehavior(CheckpointRecordKind checkpointKind)
        : base(TimelineRecordTypes.Checkpoint.Id, TimelineRecordTypes.Checkpoint.Name)
    {
        CheckpointKind = checkpointKind;
    }

    public CheckpointRecordKind CheckpointKind { get; }

    public override TimelineRecordVisibility GetVisibility(in TimelineRecord record)
    {
        return CheckpointKind != CheckpointRecordKind.User
            ? TimelineRecordVisibility.Infrastructure
            : TimelineRecordVisibility.Workflow;
    }

    public override void AddBoundaries(in TimelineRecord record, TimelineBoundaryBuilder builder)
    {
        if (builder.Scope == TimelineBoundaryScope.Traversal)
        {
            builder.AddPoint(record);
            return;
        }

        if (
            builder.Scope == TimelineBoundaryScope.StepBackward
            && builder.Playback.IsCheckpointStepBoundary(record, builder.TimedBoundaryTimes)
        )
            builder.AddPoint(record);
    }

    public override bool IsReplayMatch(
        in TimelineRecord record,
        in TimelineRecordCreateRequest request
    )
    {
        return IsRequestedBehavior(record, request)
            && request.Behavior is CheckpointRecordBehavior checkpoint
            && record.StartTime == request.StartTime
            && record.DebugLabel == request.DebugLabel
            && CheckpointKind == checkpoint.CheckpointKind;
    }

    public override bool IsEvaluatable(in TimelineRecord record, in RecordEvaluationQuery query)
    {
        return query.Direction == PlaybackDirection.Backward
            && CheckpointKind != CheckpointRecordKind.Implicit
            && record.StartTime == query.Time;
    }

    public override ValueTask EvaluateAsync(
        RecordEvaluationContext context,
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        return context.Playback.EmitCheckpointAsync(record, direction);
    }

    public override TimelineRecordInfo ToInfo(in TimelineRecord record)
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
            CheckpointKind,
            record.EntryCheckpoint?.Timestamp,
            record.EntryCheckpoint?.DeltaTime
        );
    }

    internal override bool IsStepBoundaryIncluded(
        in TimelineRecord record,
        TimelineBoundary boundary,
        PlaybackStepGranularity granularity
    )
    {
        return granularity == PlaybackStepGranularity.AwaitPoint
            || CheckpointKind == CheckpointRecordKind.User;
    }

    internal override TimelineBoundary? GetCurrentBoundaryPosition(
        in TimelineRecord record,
        TimeSpan time
    )
    {
        return TimelineBoundary.Create(record, TimelineBoundaryKind.Point);
    }

    internal override bool IsCheckpointRecord(in TimelineRecord record)
    {
        return true;
    }

    internal override bool ShouldEmitCheckpointAdded(
        in TimelineRecord record,
        bool hadEntryCheckpoint
    )
    {
        return !hadEntryCheckpoint;
    }

    internal override bool SuppressesCheckpointAutoContinuation(in TimelineRecord record)
    {
        return true;
    }

    internal override bool IsInitialPlaybackCheckpoint(
        in TimelineRecord record,
        IPlaybackRunner rootRunner
    )
    {
        return ReferenceEquals(record.OwnerRunner, rootRunner) && record.StartTime == TimeSpan.Zero;
    }
}
