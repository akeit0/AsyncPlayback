using System.Runtime.CompilerServices;

namespace MinimumPlayback;

internal interface IPlaybackRunner
{
    int Depth { get; }
    int? CallRecordIndex { get; }
    int? CurrentRecordIndex { get; }
    void AddChild(IPlaybackRunner child);
    void SetCallRecordIndex(int recordIndex);
    void MoveNext();
    void RestoreInitial();
    void RestoreStop(int stopId, int recordIndex);
    void ResetForReplay();
    void ResumeStop(int stopId);
    void CompleteAwait(int stopId, int callRecordIndex);
    void MarkCompleted();
}

internal sealed class PlaybackRunner<TStateMachine> : IPlaybackRunner
    where TStateMachine : IAsyncStateMachine
{
    private readonly Playback playback;
    private readonly PlaybackPromise promise;
    private readonly IPlaybackRunner? parent;
    private IPlaybackRunner[] children = [];
    private int childCount;
    private TStateMachine[] stops = [];
    private TStateMachine initial = default!;
    private TStateMachine current = default!;
    private int stopCount;

    public PlaybackRunner(Playback playback, PlaybackPromise promise, IPlaybackRunner? parent)
    {
        this.playback = playback;
        this.promise = promise;
        this.parent = parent;
        Depth = parent == null ? 0 : parent.Depth + 1;
        parent?.AddChild(this);
    }

    public int Depth { get; }
    public int? CallRecordIndex { get; private set; }
    public int? CurrentRecordIndex { get; private set; }

    public void SetInitial(ref TStateMachine stateMachine)
    {
        initial = SnapshotStateMachine(stateMachine);
        current = SnapshotStateMachine(stateMachine);
    }

    public void MoveNext()
    {
        using var scope = PlaybackRuntime.Push(playback, this);
        current.MoveNext();
    }

    public void AddChild(IPlaybackRunner child)
    {
        EnsureChildCapacity();
        children[childCount++] = child;
    }

    public void SetCallRecordIndex(int recordIndex)
    {
        CallRecordIndex = recordIndex;
    }

    public void RestoreInitial()
    {
        ResetForReplay();
        current = SnapshotStateMachine(initial);
        CurrentRecordIndex = null;
    }

    public void RestoreStop(int stopId, int recordIndex)
    {
        ResetForReplay();
        current = SnapshotStateMachine(stops[stopId]);
        CurrentRecordIndex = recordIndex;
    }

    public void ResetForReplay()
    {
        promise.ResetForReplay();
    }

    public int CaptureCheckpoint(ref TStateMachine stateMachine, string label)
    {
        current = SnapshotStateMachine(stateMachine);
        var stopId = AddStop(ref current);
        var recordIndex = playback.AddCheckpoint(this, stopId, label);
        CurrentRecordIndex = recordIndex;
        return recordIndex;
    }

    public int CaptureAwait(ref TStateMachine stateMachine)
    {
        var awaitState = SnapshotStateMachine(stateMachine);
        current = awaitState;
        return AddStop(ref awaitState);
    }

    public void ResumeStop(int stopId)
    {
        current = SnapshotStateMachine(stops[stopId]);
        MoveNext();
    }

    public void CompleteAwait(int stopId, int callRecordIndex)
    {
        playback.AddCallEnd(this, stopId, callRecordIndex);
    }

    public void CaptureReplaySuspension(ref TStateMachine stateMachine, int ownerRecordIndex)
    {
        current = SnapshotStateMachine(stateMachine);
        CurrentRecordIndex = ownerRecordIndex;
    }

    public void MarkCompleted()
    {
        if (parent == null)
            playback.OnRootCompleted();
    }

    private static TStateMachine SnapshotStateMachine(TStateMachine source)
    {
        if (typeof(TStateMachine).IsValueType)
            return source;

        if (source == null)
            throw new InvalidOperationException("State machine is null.");

        return CloneUtility.Clone(source);
    }

    private void EnsureChildCapacity()
    {
        if (childCount < children.Length)
            return;

        Array.Resize(ref children, children.Length == 0 ? 4 : children.Length * 2);
    }

    private int AddStop(ref TStateMachine state)
    {
        var stopId = stopCount++;
        EnsureStopCapacity(stopId + 1);
        stops[stopId] = state;
        return stopId;
    }

    private void EnsureStopCapacity(int required)
    {
        if (required <= stops.Length)
            return;

        var capacity = stops.Length == 0 ? 4 : stops.Length;
        while (capacity < required)
            capacity *= 2;
        Array.Resize(ref stops, capacity);
    }
}
