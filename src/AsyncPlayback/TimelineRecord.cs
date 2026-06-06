namespace AsyncPlayback;

public struct TimelineRecord
{
    private TimelineRecord(
        RecordId id,
        ITimelineRecordBehavior behavior,
        TimeSpan startTime,
        TimeSpan duration,
        string debugLabel
    )
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration must be non-negative."
            );
        Id = id;
        Behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        StartTime = startTime;
        Duration = duration;
        DebugLabel = string.IsNullOrWhiteSpace(debugLabel) ? behavior.TypeName : debugLabel;
        ParentIndex = null;
        ParentId = null;
        Depth = 0;
        FlatIndex = 0;
        OwnerRunner = null;
        EntryCheckpoint = null;
    }

    public RecordId Id { get; }
    public ITimelineRecordBehavior Behavior { get; }
    public TimeSpan StartTime { get; }
    public TimeSpan Duration { get; set; }
    public TimeSpan EndTime => StartTime + Duration;
    public string DebugLabel { get; }
    public int? ParentIndex { get; set; }
    public RecordId? ParentId { get; set; }
    public int Depth { get; set; }
    public int FlatIndex { get; set; }
    internal IPlaybackRunner? OwnerRunner { get; set; }
    internal TimelineCheckpoint? EntryCheckpoint { get; set; }
    public CheckpointRecordKind CheckpointKind => CheckpointBehavior.CheckpointKind;

    public static TimelineRecord Checkpoint(
        RecordId id,
        TimeSpan time,
        string debugLabel,
        CheckpointRecordKind checkpointKind
    ) => new(id, new CheckpointRecordBehavior(checkpointKind), time, TimeSpan.Zero, debugLabel);

    public static TimelineRecord Delay(
        RecordId id,
        TimeSpan startTime,
        TimeSpan duration,
        string debugLabel
    ) => new(id, new DelayRecordBehavior(), startTime, duration, debugLabel);

    public static TimelineRecord Effect(
        RecordId id,
        TimeSpan startTime,
        string debugLabel,
        Func<CancellationToken, ValueTask> executeAsync
    ) => new(id, new VoidEffectRecordBehavior(executeAsync), startTime, TimeSpan.Zero, debugLabel);

    public static TimelineRecord Effect<T>(
        RecordId id,
        TimeSpan startTime,
        string debugLabel,
        Func<CancellationToken, ValueTask<T>> executeAsync
    ) => new(id, new EffectRecordBehavior<T>(executeAsync), startTime, TimeSpan.Zero, debugLabel);

    public static TimelineRecord SeekLoop(
        RecordId id,
        TimeSpan startTime,
        TimeSpan duration,
        string debugLabel
    ) => new(id, new SeekLoopRecordBehavior(), startTime, duration, debugLabel);

    internal static TimelineRecord Call(
        RecordId id,
        TimeSpan startTime,
        string debugLabel,
        IPlaybackRunner parentRunner,
        IPlaybackRunner childRunner
    ) =>
        new(
            id,
            new CallRecordBehavior(parentRunner, childRunner),
            startTime,
            TimeSpan.Zero,
            debugLabel
        );

    public static TimelineRecord Create(
        RecordId id,
        ITimelineRecordBehavior behavior,
        TimeSpan startTime,
        TimeSpan duration,
        string debugLabel
    ) => new(id, behavior, startTime, duration, debugLabel);

    public TimelineRecordInfo ToInfo() => Behavior.ToInfo(this);

    internal EffectRecordBehavior EffectBehavior => GetEffectBehavior();
    internal IPlaybackRunner ParentRunner
    {
        get => CallBehavior.ParentRunner;
        set => CallBehavior.ParentRunner = value;
    }
    internal int ParentAwaitCheckpointId
    {
        get => CallBehavior.ParentAwaitCheckpointId;
        set => CallBehavior.ParentAwaitCheckpointId = value;
    }

    public void Complete(TimeSpan endTime)
    {
        if (endTime < StartTime)
            endTime = StartTime;
        Duration = endTime - StartTime;
    }

    internal void RebindRunners(IPlaybackRunner parentRunner, IPlaybackRunner childRunner)
    {
        ParentRunner = parentRunner;
        ParentAwaitCheckpointId = 0;
    }

    internal void BindParentAwaitCheckpoint(int checkpointId) =>
        ParentAwaitCheckpointId = checkpointId;

    private CheckpointRecordBehavior CheckpointBehavior
    {
        get
        {
            if (Behavior is not CheckpointRecordBehavior behavior)
                throw new InvalidOperationException("Record is not a checkpoint.");
            return behavior;
        }
    }

    private EffectRecordBehavior GetEffectBehavior()
    {
        if (Behavior is not EffectRecordBehavior behavior)
            throw new InvalidOperationException("Record is not an effect.");
        return behavior;
    }

    private CallRecordBehavior CallBehavior
    {
        get
        {
            if (Behavior is not CallRecordBehavior behavior)
                throw new InvalidOperationException("Record is not a call.");
            return behavior;
        }
    }
}
