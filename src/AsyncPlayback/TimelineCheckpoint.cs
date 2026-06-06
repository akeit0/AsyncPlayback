namespace AsyncPlayback;

internal sealed class TimelineCheckpoint
{
    public TimelineCheckpoint(
        long sequence,
        IPlaybackRunner runner,
        int checkpointId,
        TimeSpan time,
        Playback.IPlaybackAwaitBehavior awaitBehavior,
        PlaybackPromiseBase? awaitedPromise,
        int? resumeScope,
        int recordCountAtCapture,
        long timestamp,
        TimeSpan deltaTime
    )
    {
        Sequence = sequence;
        Runner = runner;
        CheckpointId = checkpointId;
        Time = time;
        AwaitBehavior = awaitBehavior;
        AwaitedPromise = awaitedPromise;
        ResumeScope = resumeScope;
        RecordCountAtCapture = recordCountAtCapture;
        Timestamp = timestamp;
        DeltaTime = deltaTime;
    }

    public long Sequence { get; }
    public IPlaybackRunner Runner { get; }
    public int CheckpointId { get; }
    public TimeSpan Time { get; }
    internal Playback.IPlaybackAwaitBehavior AwaitBehavior { get; }
    public PlaybackPromiseBase? AwaitedPromise { get; }
    public int? ResumeScope { get; }
    public int RecordCountAtCapture { get; }
    public long Timestamp { get; }
    public TimeSpan DeltaTime { get; }
}

internal sealed class StoreSnapshot
{
    public static StoreSnapshot Empty { get; } = new(false, null);

    public StoreSnapshot(bool hasValue, object? value)
    {
        HasValue = hasValue;
        Value = value;
    }

    public bool HasValue { get; }
    public object? Value { get; }
}
