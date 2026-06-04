namespace AsyncPlayback;

public readonly struct TimelineRecordInfo
{
    internal TimelineRecordInfo(
        int id,
        TimelineRecordKind kind,
        TimeSpan startTime,
        TimeSpan duration,
        string debugLabel,
        int? parentId,
        int depth,
        TimelineRecordVisibility visibility,
        long? timestamp,
        TimeSpan? deltaTime
    )
    {
        Id = id;
        Kind = kind;
        StartTime = startTime;
        Duration = duration;
        DebugLabel = debugLabel;
        ParentId = parentId;
        Depth = depth;
        Visibility = visibility;
        Timestamp = timestamp;
        DeltaTime = deltaTime;
    }

    public int Id { get; }
    public TimelineRecordKind Kind { get; }
    public TimeSpan StartTime { get; }
    public TimeSpan Duration { get; }
    public TimeSpan EndTime => StartTime + Duration;
    public string DebugLabel { get; }
    public int? ParentId { get; }
    public int Depth { get; }
    public TimelineRecordVisibility Visibility { get; }
    public long? Timestamp { get; }
    public TimeSpan? DeltaTime { get; }

    public override string ToString()
    {
        var indent = new string(' ', Depth * 2);
        var parent = ParentId is { } id ? $" parent=#{id}" : "";
        return $"{indent}#{Id} {Kind} {StartTime} - {EndTime}{parent}: {DebugLabel}";
    }
}

internal abstract class TimelineRecord
{
    protected TimelineRecord(
        int id,
        TimelineRecordKind kind,
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
        Kind = kind;
        StartTime = startTime;
        Duration = duration;
        DebugLabel = string.IsNullOrWhiteSpace(debugLabel) ? kind.ToString() : debugLabel;
    }

    public int Id { get; }
    public TimelineRecordKind Kind { get; }
    public TimeSpan StartTime { get; }
    public TimeSpan Duration { get; protected set; }
    public TimeSpan EndTime => StartTime + Duration;
    public string DebugLabel { get; }

    public TimelineRecord? Parent { get; internal set; }
    public List<TimelineRecord> Children { get; } = [];
    public int FlatIndex { get; internal set; }
    public IPlaybackRunner? OwnerRunner { get; internal set; }
    public TimelineCheckpoint? EntryCheckpoint { get; internal set; }

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
            Parent?.Id,
            GetDepth(),
            GetVisibility(),
            EntryCheckpoint?.Timestamp,
            EntryCheckpoint?.DeltaTime
        );
    }

    private TimelineRecordVisibility GetVisibility()
    {
        return this switch
        {
            CheckpointTimelineRecord checkpoint
                when checkpoint.CheckpointKind != CheckpointRecordKind.User =>
                TimelineRecordVisibility.Infrastructure,
            _ => TimelineRecordVisibility.Workflow,
        };
    }

    private int GetDepth()
    {
        var depth = 0;
        for (var parent = Parent; parent != null; parent = parent.Parent)
            depth++;

        return depth;
    }

    public virtual void ResetPlaybackState() { }
}

internal sealed class CheckpointTimelineRecord : TimelineRecord
{
    public CheckpointTimelineRecord(
        int id,
        TimeSpan time,
        string debugLabel,
        CheckpointRecordKind checkpointKind
    )
        : base(id, TimelineRecordKind.Checkpoint, time, TimeSpan.Zero, debugLabel)
    {
        CheckpointKind = checkpointKind;
    }

    public CheckpointRecordKind CheckpointKind { get; }
}

internal sealed class DelayRecord : TimelineRecord
{
    private PlaybackPromise? pendingDelay;

    public DelayRecord(int id, TimeSpan startTime, TimeSpan duration, string debugLabel)
        : base(id, TimelineRecordKind.Delay, startTime, duration, debugLabel) { }

    public bool HasPendingDelay => pendingDelay is { IsCompleted: false };

    public void ArmDelay(PlaybackPromise promise)
    {
        pendingDelay = promise ?? throw new ArgumentNullException(nameof(promise));
    }

    public bool Complete()
    {
        var promise = pendingDelay;

        if (promise is not { IsCompleted: false })
            return false;

        pendingDelay = null;
        return promise.TrySetResult();
    }

    public override void ResetPlaybackState()
    {
        pendingDelay = null;
    }
}

internal sealed class EffectRecord : TimelineRecord
{
    public EffectRecord(
        int id,
        TimeSpan startTime,
        string debugLabel,
        Func<CancellationToken, ValueTask<object?>> executeAsync
    )
        : base(id, TimelineRecordKind.Effect, startTime, TimeSpan.Zero, debugLabel)
    {
        ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }

    public Func<CancellationToken, ValueTask<object?>> ExecuteAsync { get; }

    public void Complete(TimeSpan endTime)
    {
        if (endTime < StartTime)
            endTime = StartTime;

        Duration = endTime - StartTime;
    }
}

internal sealed class SeekLoopRecord : TimelineRecord
{
    private PlaybackPromise<bool>? pendingMoveNext;

    public SeekLoopRecord(int id, TimeSpan startTime, TimeSpan duration, string debugLabel)
        : base(id, TimelineRecordKind.SeekLoop, startTime, duration, debugLabel) { }

    public TimeSpan CurrentElapsed { get; private set; }
    public bool FinalTrueDelivered { get; private set; }

    public bool HasPendingMoveNext => pendingMoveNext is { IsCompleted: false };

    public void ArmMoveNext(PlaybackPromise<bool> promise)
    {
        pendingMoveNext = promise ?? throw new ArgumentNullException(nameof(promise));
    }

    public bool EmitTrueAt(TimeSpan playbackTime)
    {
        var promise = pendingMoveNext;

        if (promise is not { IsCompleted: false })
            return false;

        var elapsed = playbackTime - StartTime;

        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed > Duration)
            elapsed = Duration;

        CurrentElapsed = elapsed;

        if (elapsed == Duration)
            FinalTrueDelivered = true;

        pendingMoveNext = null;
        return promise.TrySetResult(true);
    }

    public bool CompleteFalse()
    {
        var promise = pendingMoveNext;

        if (promise is not { IsCompleted: false })
            return false;

        pendingMoveNext = null;
        return promise.TrySetResult(false);
    }

    public override void ResetPlaybackState()
    {
        pendingMoveNext = null;
        CurrentElapsed = default;
        FinalTrueDelivered = false;
    }
}

internal sealed class CallTimelineRecord : TimelineRecord
{
    public CallTimelineRecord(
        int id,
        TimeSpan startTime,
        string debugLabel,
        IPlaybackRunner parentRunner,
        IPlaybackRunner childRunner
    )
        : base(id, TimelineRecordKind.Call, startTime, TimeSpan.Zero, debugLabel)
    {
        ParentRunner = parentRunner;
        ChildRunner = childRunner;
    }

    public IPlaybackRunner ParentRunner { get; private set; }
    public IPlaybackRunner ChildRunner { get; private set; }
    public int ParentAwaitCheckpointId { get; private set; }

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

    public void Complete(TimeSpan endTime)
    {
        if (endTime < StartTime)
            endTime = StartTime;

        Duration = endTime - StartTime;
    }
}
