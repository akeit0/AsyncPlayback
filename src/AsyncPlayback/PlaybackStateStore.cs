namespace AsyncPlayback;

internal sealed class PlaybackStateStore
{
    private readonly Dictionary<RunnerCheckpointKey, int> checkpointSlotIndexes = [];
    private readonly List<StoreSnapshot> entrySnapshots = [];
    private readonly List<StoreSnapshot?> resumeSnapshots = [];

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
        checkpointSlotIndexes.Clear();
        entrySnapshots.Clear();
        resumeSnapshots.Clear();
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
        if (!TryGetSlotIndex(checkpoint, out var slotIndex))
        {
            Restore(null);
            return;
        }

        Restore(resumeSnapshots[slotIndex] ?? entrySnapshots[slotIndex]);
    }

    public void RestoreEntry(TimelineCheckpoint checkpoint)
    {
        Restore(TryGetSlotIndex(checkpoint, out var slotIndex) ? entrySnapshots[slotIndex] : null);
    }

    public void RestoreEntry(int slotIndex)
    {
        Restore(IsValidSlotIndex(slotIndex) ? entrySnapshots[slotIndex] : null);
    }

    public void CaptureAtCurrentRunner()
    {
        if (TryGetCurrentSlotIndex(out var slotIndex))
            resumeSnapshots[slotIndex] = CaptureSnapshot();
    }

    public void StoreAtCurrentRunnerIfMissing<T>(T state)
        where T : notnull
    {
        var hasSlot = TryGetCurrentSlotIndex(out var slotIndex);
        if (hasSlot && resumeSnapshots[slotIndex] != null)
        {
            Restore(resumeSnapshots[slotIndex]);
            return;
        }

        Store(state);

        if (hasSlot)
            resumeSnapshots[slotIndex] = CaptureSnapshot();
    }

    public void SetEntrySnapshot(int slotIndex, StoreSnapshot snapshot)
    {
        EnsureSlotIndex(slotIndex);
        entrySnapshots[slotIndex] = snapshot;
    }

    public void Bind(IPlaybackRunner runner, int checkpointId, int slotIndex)
    {
        EnsureSlotIndex(slotIndex);
        checkpointSlotIndexes[new(runner, checkpointId)] = slotIndex;
    }

    public void Bind(TimelineCheckpoint checkpoint, int slotIndex)
    {
        Bind(checkpoint.Runner, checkpoint.CheckpointId, slotIndex);
    }

    public void BindNewSlot(TimelineCheckpoint checkpoint, StoreSnapshot snapshot)
    {
        var slotIndex = entrySnapshots.Count;
        SetEntrySnapshot(slotIndex, snapshot);
        Bind(checkpoint, slotIndex);
    }

    private bool TryGetCurrentSlotIndex(out int slotIndex)
    {
        var runner = PlaybackRuntime.CurrentRunner;
        if (runner == null)
        {
            slotIndex = -1;
            return false;
        }

        return checkpointSlotIndexes.TryGetValue(
            new(runner, runner.CurrentStateCheckpointId),
            out slotIndex
        );
    }

    private bool TryGetSlotIndex(TimelineCheckpoint checkpoint, out int slotIndex)
    {
        return checkpointSlotIndexes.TryGetValue(
            new(checkpoint.Runner, checkpoint.CheckpointId),
            out slotIndex
        );
    }

    private void EnsureSlotIndex(int slotIndex)
    {
        if (slotIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));

        while (entrySnapshots.Count <= slotIndex)
        {
            entrySnapshots.Add(StoreSnapshot.Empty);
            resumeSnapshots.Add(null);
        }
    }

    private bool IsValidSlotIndex(int slotIndex)
    {
        return slotIndex >= 0 && slotIndex < entrySnapshots.Count;
    }

    private readonly record struct RunnerCheckpointKey(IPlaybackRunner Runner, int CheckpointId);
}
