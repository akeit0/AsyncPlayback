namespace AsyncPlayback;

internal sealed class TimelineRecordRuntime
{
    private readonly Dictionary<DelayRecord, PlaybackPromise> pendingDelays = [];
    private readonly Dictionary<SeekLoopRecord, SeekLoopRuntimeState> seekLoops = [];

    public bool HasPendingDelay(DelayRecord record)
    {
        return pendingDelays.TryGetValue(record, out var promise) && !promise.IsCompleted;
    }

    public void ArmDelay(DelayRecord record, PlaybackPromise promise)
    {
        pendingDelays[record] = promise ?? throw new ArgumentNullException(nameof(promise));
    }

    public bool CompleteDelay(DelayRecord record)
    {
        if (!pendingDelays.TryGetValue(record, out var promise) || promise.IsCompleted)
            return false;

        pendingDelays.Remove(record);
        return promise.TrySetResult();
    }

    public bool HasPendingSeekLoopMoveNext(SeekLoopRecord record)
    {
        return seekLoops.TryGetValue(record, out var state)
            && state.PendingMoveNext is { IsCompleted: false };
    }

    public TimeSpan GetSeekLoopElapsed(SeekLoopRecord record)
    {
        return seekLoops.TryGetValue(record, out var state) ? state.CurrentElapsed : default;
    }

    public bool HasDeliveredFinalSeekLoopTrue(SeekLoopRecord record)
    {
        return seekLoops.TryGetValue(record, out var state) && state.FinalTrueDelivered;
    }

    public void ArmSeekLoopMoveNext(SeekLoopRecord record, PlaybackPromise<bool> promise)
    {
        var state = GetOrCreateSeekLoopState(record);
        state.PendingMoveNext = promise ?? throw new ArgumentNullException(nameof(promise));
    }

    public bool EmitSeekLoopTrueAt(SeekLoopRecord record, TimeSpan playbackTime)
    {
        var state = GetOrCreateSeekLoopState(record);
        var promise = state.PendingMoveNext;

        if (promise is not { IsCompleted: false })
            return false;

        var elapsed = playbackTime - record.StartTime;

        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed > record.Duration)
            elapsed = record.Duration;

        state.CurrentElapsed = elapsed;

        if (elapsed == record.Duration)
            state.FinalTrueDelivered = true;

        state.PendingMoveNext = null;
        return promise.TrySetResult(true);
    }

    public bool CompleteSeekLoopFalse(SeekLoopRecord record)
    {
        if (!seekLoops.TryGetValue(record, out var state))
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

    public void TrimTo(IReadOnlyCollection<TimelineRecord> liveRecords)
    {
        var live = new HashSet<TimelineRecord>(liveRecords);

        foreach (var record in pendingDelays.Keys.ToArray())
            if (!live.Contains(record))
                pendingDelays.Remove(record);

        foreach (var record in seekLoops.Keys.ToArray())
            if (!live.Contains(record))
                seekLoops.Remove(record);
    }

    private SeekLoopRuntimeState GetOrCreateSeekLoopState(SeekLoopRecord record)
    {
        if (!seekLoops.TryGetValue(record, out var state))
        {
            state = new();
            seekLoops.Add(record, state);
        }

        return state;
    }

    private sealed class SeekLoopRuntimeState
    {
        public PlaybackPromise<bool>? PendingMoveNext { get; set; }
        public TimeSpan CurrentElapsed { get; set; }
        public bool FinalTrueDelivered { get; set; }
    }
}
