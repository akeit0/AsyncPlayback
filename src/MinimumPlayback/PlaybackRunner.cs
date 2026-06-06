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
    void RestoreRecord(int recordIndex);
    void BindRecord(int checkpointId, int recordIndex);
    void MarkCompleted();
    IPlaybackRunner? FindRunnerForRecord(int recordIndex);
}

internal sealed class PlaybackRunner<TStateMachine> : IPlaybackRunner
    where TStateMachine : IAsyncStateMachine
{
    private readonly Playback playback;
    private readonly PlaybackPromise promise;
    private readonly IPlaybackRunner? parent;
    private IPlaybackRunner[] children = [];
    private int childCount;
    private CheckpointSlot[] checkpoints = [];
    private TStateMachine initial = default!;
    private TStateMachine current = default!;
    private int nextCheckpointId;

    public PlaybackRunner(Playback playback, PlaybackPromise promise, IPlaybackRunner? parent)
    {
        this.playback = playback;
        this.promise = promise;
        this.parent = parent;
        Depth = parent == null ? 0 : parent.Depth + 1;
        CallRecordIndex = parent == null ? null : playback.AddCall(parent, "Call");
        parent?.AddChild(this);
    }

    public int Depth { get; }
    public int? CallRecordIndex { get; }
    public int? CurrentRecordIndex { get; private set; }

    public void SetInitial(ref TStateMachine stateMachine)
    {
        initial = stateMachine;
        current = stateMachine;
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
        current = initial;
        CurrentRecordIndex = null;
    }

    public void RestoreRecord(int recordIndex)
    {
        var checkpoint = FindCheckpoint(recordIndex);
        current = checkpoint.State;
        CurrentRecordIndex = recordIndex;
    }

    public void BindRecord(int checkpointId, int recordIndex)
    {
        var checkpoint = checkpoints[checkpointId];
        checkpoint.RecordIndex = recordIndex;
        checkpoints[checkpointId] = checkpoint;
        CurrentRecordIndex = recordIndex;
    }

    public int CaptureCheckpoint(ref TStateMachine stateMachine, string label)
    {
        current = stateMachine;
        var checkpointId = nextCheckpointId++;
        EnsureCheckpointCapacity(checkpointId + 1);
        ref var checkpoint = ref checkpoints[checkpointId];
        checkpoint.State = stateMachine;
        checkpoint.RecordIndex = -1;
        return playback.AddCheckpoint(this, checkpointId, label);
    }

    public void CaptureAwait(ref TStateMachine stateMachine)
    {
        current = stateMachine;
    }

    public void CaptureReplaySuspension(ref TStateMachine stateMachine, int ownerRecordIndex)
    {
        current = stateMachine;
        CurrentRecordIndex = ownerRecordIndex;
    }

    public void MarkCompleted()
    {
        if (parent == null)
            playback.OnRootCompleted();
        promise.TrySetResult();
    }

    public IPlaybackRunner? FindRunnerForRecord(int recordIndex)
    {
        if (HasCheckpoint(recordIndex))
            return this;

        for (var i = 0; i < childCount; i++)
        {
            var child = children[i];
            if (child.FindRunnerForRecord(recordIndex) is { } runner)
                return runner;
        }

        return null;
    }

    private CheckpointSlot FindCheckpoint(int recordIndex)
    {
        for (var i = 0; i < nextCheckpointId; i++)
        {
            var checkpoint = checkpoints[i];
            if (checkpoint.RecordIndex == recordIndex)
                return checkpoint;
        }

        throw new InvalidOperationException("Checkpoint record was not found.");
    }

    private bool HasCheckpoint(int recordIndex)
    {
        for (var i = 0; i < nextCheckpointId; i++)
        {
            var checkpoint = checkpoints[i];
            if (checkpoint.RecordIndex == recordIndex)
                return true;
        }

        return false;
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

    private struct CheckpointSlot
    {
        public CheckpointSlot(TStateMachine state, int recordIndex)
        {
            State = state;
            RecordIndex = recordIndex;
        }

        public TStateMachine State;
        public int RecordIndex;
    }
}
