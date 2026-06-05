using System.Runtime.InteropServices;

namespace AsyncPlayback;

internal sealed class TimelineRecordRuntime
{
    private readonly List<PlaybackPromise?> pendingDelays = [];
    private readonly List<SeekLoopRuntimeState> seekLoops = [];

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
            && seekLoops[recordIndex].PendingMoveNext is { IsCompleted: false };
    }

    public TimeSpan GetSeekLoopElapsed(int recordIndex)
    {
        return recordIndex < seekLoops.Count ? seekLoops[recordIndex].CurrentElapsed : default;
    }

    public bool HasDeliveredFinalSeekLoopTrue(int recordIndex)
    {
        return recordIndex < seekLoops.Count && seekLoops[recordIndex].FinalTrueDelivered;
    }

    public void ArmSeekLoopMoveNext(int recordIndex, PlaybackPromise<bool> promise)
    {
        ref var state = ref GetSeekLoopStateSlot(recordIndex);
        state.PendingMoveNext = promise ?? throw new ArgumentNullException(nameof(promise));
    }

    public bool EmitSeekLoopTrueAt(
        int recordIndex,
        TimeSpan recordStartTime,
        TimeSpan recordDuration,
        TimeSpan playbackTime
    )
    {
        ref var state = ref GetSeekLoopStateSlot(recordIndex);
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

    public void PrepareSeekLoopReplayAt(
        int recordIndex,
        TimeSpan recordStartTime,
        TimeSpan recordDuration,
        TimeSpan playbackTime
    )
    {
        ref var state = ref GetSeekLoopStateSlot(recordIndex);
        var elapsed = ClampElapsed(recordStartTime, recordDuration, playbackTime);
        state.CurrentElapsed = elapsed;
        state.ReplayTrueRemaining = 1;

        if (elapsed == recordDuration)
            state.FinalTrueDelivered = true;
    }

    public bool ConsumeReplaySeekLoopMoveNext(int recordIndex)
    {
        ref var state = ref GetSeekLoopStateSlot(recordIndex);
        if (state.ReplayTrueRemaining <= 0)
            return false;

        state.ReplayTrueRemaining--;
        return true;
    }

    public bool CompleteSeekLoopFalse(int recordIndex)
    {
        if (recordIndex >= seekLoops.Count)
            return false;

        var state = seekLoops[recordIndex];
        var promise = state.PendingMoveNext;

        if (promise is not { IsCompleted: false })
            return false;

        state.PendingMoveNext = null;
        seekLoops[recordIndex] = state;
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

    private ref SeekLoopRuntimeState GetSeekLoopStateSlot(int recordIndex)
    {
        EnsureRecordIndex(recordIndex);
        return ref CollectionsMarshal.AsSpan(seekLoops)[recordIndex];
    }

    private void EnsureRecordIndex(int recordIndex)
    {
        if (recordIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(recordIndex));

        while (pendingDelays.Count <= recordIndex)
            pendingDelays.Add(null);

        while (seekLoops.Count <= recordIndex)
            seekLoops.Add(default);
    }

    private static TimeSpan ClampElapsed(
        TimeSpan recordStartTime,
        TimeSpan recordDuration,
        TimeSpan playbackTime
    )
    {
        var elapsed = playbackTime - recordStartTime;

        if (elapsed < TimeSpan.Zero)
            return TimeSpan.Zero;

        if (elapsed > recordDuration)
            return recordDuration;

        return elapsed;
    }

    private struct SeekLoopRuntimeState
    {
        public PlaybackPromise<bool>? PendingMoveNext { get; set; }
        public TimeSpan CurrentElapsed { get; set; }
        public bool FinalTrueDelivered { get; set; }
        public int ReplayTrueRemaining { get; set; }
    }
}
