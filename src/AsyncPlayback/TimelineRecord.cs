namespace AsyncPlayback;

internal struct TimelineRecord
{
    private TimelineRecord(
        RecordId id,
        TimelineRecordKind kind,
        TimeSpan startTime,
        TimeSpan duration,
        string debugLabel,
        CheckpointRecordKind checkpointKind = default,
        IRecordPayload? payload = null
    )
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration must be non-negative."
            );

        Id = id;
        Kind = kind;
        StartTime = startTime;
        Duration = duration;
        DebugLabel = string.IsNullOrWhiteSpace(debugLabel) ? kind.ToString() : debugLabel;
        CheckpointKind = checkpointKind;
        Payload = payload;
        ParentIndex = null;
        ParentId = null;
        Depth = 0;
        FlatIndex = 0;
        OwnerRunner = null;
        EntryCheckpoint = null;
    }

    public RecordId Id { get; }
    public TimelineRecordKind Kind { get; }
    public TimeSpan StartTime { get; }
    public TimeSpan Duration { get; set; }
    public TimeSpan EndTime => StartTime + Duration;
    public string DebugLabel { get; }
    public CheckpointRecordKind CheckpointKind { get; }
    public IRecordPayload? Payload { get; }

    public int? ParentIndex { get; set; }
    public RecordId? ParentId { get; set; }
    public int Depth { get; set; }
    public int FlatIndex { get; set; }
    public IPlaybackRunner? OwnerRunner { get; set; }
    public TimelineCheckpoint? EntryCheckpoint { get; set; }

    public static TimelineRecord Checkpoint(
        RecordId id,
        TimeSpan time,
        string debugLabel,
        CheckpointRecordKind checkpointKind
    )
    {
        return new(
            id,
            TimelineRecordKind.Checkpoint,
            time,
            TimeSpan.Zero,
            debugLabel,
            checkpointKind
        );
    }

    public static TimelineRecord Delay(
        RecordId id,
        TimeSpan startTime,
        TimeSpan duration,
        string debugLabel
    )
    {
        return new(id, TimelineRecordKind.Delay, startTime, duration, debugLabel);
    }

    public static TimelineRecord Effect(
        RecordId id,
        TimeSpan startTime,
        string debugLabel,
        Func<CancellationToken, ValueTask<object?>> executeAsync
    )
    {
        return new(
            id,
            TimelineRecordKind.Effect,
            startTime,
            TimeSpan.Zero,
            debugLabel,
            payload: new EffectRecordPayload(executeAsync)
        );
    }

    public static TimelineRecord SeekLoop(
        RecordId id,
        TimeSpan startTime,
        TimeSpan duration,
        string debugLabel
    )
    {
        return new(id, TimelineRecordKind.SeekLoop, startTime, duration, debugLabel);
    }

    public static TimelineRecord Call(
        RecordId id,
        TimeSpan startTime,
        string debugLabel,
        IPlaybackRunner parentRunner,
        IPlaybackRunner childRunner
    )
    {
        return new(
            id,
            TimelineRecordKind.Call,
            startTime,
            TimeSpan.Zero,
            debugLabel,
            payload: new CallRecordPayload(parentRunner, childRunner)
        );
    }

    public bool Contains(TimeSpan time, bool includeEnd = true)
    {
        return includeEnd
            ? StartTime <= time && time <= EndTime
            : StartTime <= time && time < EndTime;
    }

    public TimelineRecordInfo ToInfo()
    {
        return new(
            Id,
            Kind,
            StartTime,
            Duration,
            DebugLabel,
            ParentId,
            Depth,
            GetVisibility(),
            Kind == TimelineRecordKind.Checkpoint ? CheckpointKind : null,
            EntryCheckpoint?.Timestamp,
            EntryCheckpoint?.DeltaTime
        );
    }

    private TimelineRecordVisibility GetVisibility()
    {
        return Kind == TimelineRecordKind.Checkpoint && CheckpointKind != CheckpointRecordKind.User
            ? TimelineRecordVisibility.Infrastructure
            : TimelineRecordVisibility.Workflow;
    }

    public bool IsCheckpoint(CheckpointRecordKind kind)
    {
        return Kind == TimelineRecordKind.Checkpoint && CheckpointKind == kind;
    }

    public Func<CancellationToken, ValueTask<object?>> ExecuteAsync => EffectPayload.ExecuteAsync;

    public bool TryGetEffectResult(out object? result)
    {
        var payload = EffectPayload;
        result = payload.Result;
        return payload.HasResult;
    }

    public void SetEffectResult(object? result)
    {
        var payload = EffectPayload;
        payload.Result = result;
        payload.HasResult = true;
    }

    public IPlaybackRunner ParentRunner
    {
        get => CallPayload.ParentRunner;
        set => CallPayload.ParentRunner = value;
    }

    public IPlaybackRunner ChildRunner
    {
        get => CallPayload.ChildRunner;
        set => CallPayload.ChildRunner = value;
    }

    public int ParentAwaitCheckpointId
    {
        get => CallPayload.ParentAwaitCheckpointId;
        set => CallPayload.ParentAwaitCheckpointId = value;
    }

    public void Complete(TimeSpan endTime)
    {
        if (endTime < StartTime)
            endTime = StartTime;

        Duration = endTime - StartTime;
    }

    public void RebindRunners(IPlaybackRunner parentRunner, IPlaybackRunner childRunner)
    {
        ParentRunner = parentRunner;
        ChildRunner = childRunner;
        ParentAwaitCheckpointId = 0;
    }

    public void BindParentAwaitCheckpoint(int checkpointId)
    {
        ParentAwaitCheckpointId = checkpointId;
    }

    private EffectRecordPayload EffectPayload
    {
        get
        {
            if (Kind != TimelineRecordKind.Effect || Payload is not EffectRecordPayload payload)
                throw new InvalidOperationException("Record is not an effect.");

            return payload;
        }
    }

    private CallRecordPayload CallPayload
    {
        get
        {
            if (Kind != TimelineRecordKind.Call || Payload is not CallRecordPayload payload)
                throw new InvalidOperationException("Record is not a call.");

            return payload;
        }
    }
}
