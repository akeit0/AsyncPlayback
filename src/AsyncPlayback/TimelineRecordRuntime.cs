namespace AsyncPlayback;

internal sealed class TimelineRecordRuntime
{
    private readonly List<PlaybackPromise?> pendingDelays = [];
    private readonly List<SeekLoopRuntimeState?> seekLoops = [];

    public bool HasPendingDelay(int recordIndex)
    {
        return recordIndex < pendingDelays.Count
            && pendingDelays[recordIndex] is { IsCompleted: false };
    }

    public void ArmDelay(int recordIndex, PlaybackPromise promise)
    {
        EnsureRecordIndex(recordIndex);
        pendingDelays[recordIndex] = promise ?? throw new ArgumentNullException(nameof(promise));
    }

    public bool CompleteDelay(int recordIndex)
    {
        if (
            recordIndex >= pendingDelays.Count
            || pendingDelays[recordIndex] is not { IsCompleted: false } promise
        )
            return false;

        pendingDelays[recordIndex] = null;
        return promise.TrySetResult();
    }

    public bool HasPendingSeekLoopMoveNext(int recordIndex)
    {
        return recordIndex < seekLoops.Count
            && seekLoops[recordIndex]?.PendingMoveNext is { IsCompleted: false };
    }

    public TimeSpan GetSeekLoopElapsed(int recordIndex)
    {
        return recordIndex < seekLoops.Count && seekLoops[recordIndex] is { } state
            ? state.CurrentElapsed
            : default;
    }

    public bool HasDeliveredFinalSeekLoopTrue(int recordIndex)
    {
        return recordIndex < seekLoops.Count && seekLoops[recordIndex]?.FinalTrueDelivered == true;
    }

    public void ArmSeekLoopMoveNext(int recordIndex, PlaybackPromise<bool> promise)
    {
        var state = GetOrCreateSeekLoopState(recordIndex);
        state.PendingMoveNext = promise ?? throw new ArgumentNullException(nameof(promise));
    }

    public bool EmitSeekLoopTrueAt(
        int recordIndex,
        TimeSpan recordStartTime,
        TimeSpan recordDuration,
        TimeSpan playbackTime
    )
    {
        var state = GetOrCreateSeekLoopState(recordIndex);
        var promise = state.PendingMoveNext;

        if (promise is not { IsCompleted: false })
            return false;

        var elapsed = playbackTime - recordStartTime;

        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed > recordDuration)
            elapsed = recordDuration;

        state.CurrentElapsed = elapsed;

        if (elapsed == recordDuration)
            state.FinalTrueDelivered = true;

        state.PendingMoveNext = null;
        return promise.TrySetResult(true);
    }

    public bool CompleteSeekLoopFalse(int recordIndex)
    {
        if (recordIndex >= seekLoops.Count || seekLoops[recordIndex] is not { } state)
            return false;

        var promise = state.PendingMoveNext;

        if (promise is not { IsCompleted: false })
            return false;

        state.PendingMoveNext = null;
        return promise.TrySetResult(false);
    }

    public void Reset()
    {
        pendingDelays.Clear();
        seekLoops.Clear();
    }

    public void TrimTo(int recordCount)
    {
        if (recordCount < 0)
            recordCount = 0;

        if (pendingDelays.Count > recordCount)
            pendingDelays.RemoveRange(recordCount, pendingDelays.Count - recordCount);

        if (seekLoops.Count > recordCount)
            seekLoops.RemoveRange(recordCount, seekLoops.Count - recordCount);
    }

    private SeekLoopRuntimeState GetOrCreateSeekLoopState(int recordIndex)
    {
        EnsureRecordIndex(recordIndex);

        if (seekLoops[recordIndex] == null)
        {
            seekLoops[recordIndex] = new();
        }

        return seekLoops[recordIndex]!;
    }

    private void EnsureRecordIndex(int recordIndex)
    {
        if (recordIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(recordIndex));

        while (pendingDelays.Count <= recordIndex)
            pendingDelays.Add(null);

        while (seekLoops.Count <= recordIndex)
            seekLoops.Add(null);
    }

    private sealed class SeekLoopRuntimeState
    {
        public PlaybackPromise<bool>? PendingMoveNext { get; set; }
        public TimeSpan CurrentElapsed { get; set; }
        public bool FinalTrueDelivered { get; set; }
    }
}
