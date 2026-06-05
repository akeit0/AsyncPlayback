namespace AsyncPlayback;

internal sealed class PlaybackStateStore
{
    private readonly Dictionary<RunnerCheckpointKey, TimelineCheckpoint> checkpoints = [];

    private object? value;
    private bool hasValue;

    public void Store(object? state)
    {
        value = state;
        hasValue = true;
    }

    public void Clear()
    {
        value = null;
        hasValue = false;
    }

    public void Reset()
    {
        checkpoints.Clear();
        Clear();
    }

    public bool TryGet<T>(out T state)
    {
        if (!hasValue)
        {
            state = default!;
            return false;
        }

        if (value == null)
        {
            state = default!;
            return default(T) is null;
        }

        if (value is not T typed)
        {
            state = default!;
            return false;
        }

        state = typed;
        return true;
    }

    public StoreSnapshot CaptureSnapshot()
    {
        return new(hasValue, value);
    }

    public void Restore(StoreSnapshot? snapshot)
    {
        if (snapshot is { HasValue: true })
        {
            value = snapshot.Value;
            hasValue = true;
            return;
        }

        Clear();
    }

    public void RestoreResume(TimelineCheckpoint checkpoint)
    {
        Restore(checkpoint.ResumeStoreSnapshot ?? checkpoint.StoreSnapshot);
    }

    public void CaptureAtCurrentRunner()
    {
        if (TryGetCurrentCheckpoint(out var checkpoint))
            checkpoint.ResumeStoreSnapshot = CaptureSnapshot();
    }

    public void StoreAtCurrentRunnerIfMissing<T>(T state)
        where T : notnull
    {
        var hasCheckpoint = TryGetCurrentCheckpoint(out var checkpoint);
        if (hasCheckpoint && checkpoint.ResumeStoreSnapshot != null)
        {
            Restore(checkpoint.ResumeStoreSnapshot);
            return;
        }

        Store(state);

        if (hasCheckpoint)
            checkpoint.ResumeStoreSnapshot = CaptureSnapshot();
    }

    public void Bind(TimelineCheckpoint checkpoint)
    {
        Bind(checkpoint.Runner, checkpoint.CheckpointId, checkpoint);
    }

    public void Bind(IPlaybackRunner runner, int checkpointId, TimelineCheckpoint checkpoint)
    {
        checkpoints[new(runner, checkpointId)] = checkpoint;
    }

    private bool TryGetCurrentCheckpoint(out TimelineCheckpoint checkpoint)
    {
        var runner = PlaybackRuntime.CurrentRunner;
        if (runner == null)
        {
            checkpoint = null!;
            return false;
        }

        return checkpoints.TryGetValue(new(runner, runner.CurrentStateCheckpointId), out checkpoint!);
    }

    private readonly record struct RunnerCheckpointKey(IPlaybackRunner Runner, int CheckpointId);
}
