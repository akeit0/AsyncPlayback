using System.Runtime.CompilerServices;

namespace MinimumPlayback;

internal interface IPlaybackRunner
{
    int Depth { get; }
    int? CallRecordIndex { get; }
    int? CurrentRecordIndex { get; }
    void AddChild(IPlaybackRunner child);
    void MoveNext();
    void RestoreInitial();
    void RestoreCheckpoint(int checkpointId, int recordIndex);
    void ResetForReplay();
    void ResumeContinuation(int continuationId);
    void CompleteAwait(int continuationId, int callRecordIndex);
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
    private TStateMachine[] checkpoints = [];
    private TStateMachine[] continuations = [];
    private TStateMachine initial = default!;
    private TStateMachine current = default!;
    private int nextCheckpointId;
    private int continuationCount;

    public PlaybackRunner(Playback playback, PlaybackPromise promise, IPlaybackRunner? parent)
    {
        this.playback = playback;
        this.promise = promise;
        this.parent = parent;
        Depth = parent == null ? 0 : parent.Depth + 1;
        CallRecordIndex = parent == null ? null : playback.AddCall(parent, "In");
        parent?.AddChild(this);
    }

    public int Depth { get; }
    public int? CallRecordIndex { get; }
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

    public void RestoreInitial()
    {
        ResetForReplay();
        current = SnapshotStateMachine(initial);
        CurrentRecordIndex = null;
    }

    public void RestoreCheckpoint(int checkpointId, int recordIndex)
    {
        ResetForReplay();
        current = SnapshotStateMachine(checkpoints[checkpointId]);
        CurrentRecordIndex = recordIndex;
    }

    public void ResetForReplay()
    {
        promise.ResetForReplay();
    }

    public int CaptureCheckpoint(ref TStateMachine stateMachine, string label)
    {
        current = SnapshotStateMachine(stateMachine);
        var checkpointId = nextCheckpointId++;
        EnsureCheckpointCapacity(checkpointId + 1);
        checkpoints[checkpointId] = current;
        var recordIndex = playback.AddCheckpoint(this, checkpointId, label);
        CurrentRecordIndex = recordIndex;
        return recordIndex;
    }

    public int CaptureAwait(ref TStateMachine stateMachine)
    {
        var awaitState = SnapshotStateMachine(stateMachine);
        current = awaitState;
        var continuationId = continuationCount++;
        EnsureContinuationCapacity(continuationId + 1);
        continuations[continuationId] = awaitState;
        return continuationId;
    }

    public void ResumeContinuation(int continuationId)
    {
        current = SnapshotStateMachine(continuations[continuationId]);
        MoveNext();
    }

    public void CompleteAwait(int continuationId, int callRecordIndex)
    {
        playback.AddCallEnd(this, continuationId, callRecordIndex);
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

    private void EnsureCheckpointCapacity(int required)
    {
        if (required <= checkpoints.Length)
            return;

        var capacity = checkpoints.Length == 0 ? 4 : checkpoints.Length;
        while (capacity < required)
            capacity *= 2;
        Array.Resize(ref checkpoints, capacity);
    }

    private void EnsureContinuationCapacity(int required)
    {
        if (required <= continuations.Length)
            return;

        var capacity = continuations.Length == 0 ? 4 : continuations.Length;
        while (capacity < required)
            capacity *= 2;
        Array.Resize(ref continuations, capacity);
    }
}
