namespace AsyncPlayback;

internal sealed class TimelineCheckpoint
{
    public TimelineCheckpoint(
        long sequence,
        IPlaybackRunner runner,
        int checkpointId,
        TimeSpan time,
        PlaybackPromiseKind awaitKind,
        PlaybackPromiseBase? awaitedPromise,
        TimelineRecord? resumeScope,
        int recordCountAtCapture,
        StoreSnapshot storeSnapshot,
        long timestamp,
        TimeSpan deltaTime
    )
    {
        Sequence = sequence;
        Runner = runner;
        CheckpointId = checkpointId;
        Time = time;
        AwaitKind = awaitKind;
        AwaitedPromise = awaitedPromise;
        ResumeScope = resumeScope;
        RecordCountAtCapture = recordCountAtCapture;
        StoreSnapshot = storeSnapshot;
        Timestamp = timestamp;
        DeltaTime = deltaTime;
    }

    public long Sequence { get; }
    public IPlaybackRunner Runner { get; }
    public int CheckpointId { get; }
    public TimeSpan Time { get; }
    public PlaybackPromiseKind AwaitKind { get; }
    public PlaybackPromiseBase? AwaitedPromise { get; }
    public TimelineRecord? ResumeScope { get; }
    public int RecordCountAtCapture { get; }
    public StoreSnapshot StoreSnapshot { get; internal set; }
    public long Timestamp { get; }
    public TimeSpan DeltaTime { get; }
}

internal sealed class StoreSnapshot
{
    public StoreSnapshot(bool hasValue, object? value)
    {
        HasValue = hasValue;
        Value = value;
    }

    public bool HasValue { get; }
    public object? Value { get; }
}
