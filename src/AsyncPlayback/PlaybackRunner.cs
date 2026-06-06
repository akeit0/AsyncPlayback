using System.Reflection;
using System.Runtime.CompilerServices;

namespace AsyncPlayback;

internal interface IPlaybackRunner
{
    long Epoch { get; }
    PlaybackPromiseBase OwnPromise { get; }
    IPlaybackRunner? ParentRunner { get; }
    int? CurrentCallRecordIndex { get; }
    int ParentAwaitCheckpointId { get; }
    int CurrentStateCheckpointId { get; }

    TimelineRecord this[int recordIndex] { get; }

    int? GetResumeScope(int checkpointId);

    void BindCurrentCallRecord(int callRecordIndex);
    void SetParentAwaitCheckpointId(int checkpointId);

    void MoveNext();
    void ResumeFromAwait(int checkpointId, long expectedEpoch, int? resumeScopeIndex);

    void RestoreCheckpoint(int checkpointId);
    void RestoreInitialCheckpoint();
    void MarkCompleted();
}

class CloneUtility
{
    public static T Clone<T>(T obj)
    {
        var o = Unsafe.As<CloneUtility>((object)obj!);
        return (T)o.MemberwiseClone();
    }
}

internal sealed class PlaybackRunner<TStateMachine> : IPlaybackRunner
    where TStateMachine : IAsyncStateMachine
{
    private TStateMachine[] checkpoints = [];
    private int?[] checkpointScopes = [];
    private readonly TStateMachine initialSnapshot;

    private readonly Playback playback;

    private TStateMachine current;
    private bool isCompleted;
    private int nextCheckpointId;
    private int? scopeForNextMoveNext;
    private int suspendedCheckpointId;

    public PlaybackRunner(
        Playback playback,
        PlaybackPromiseBase ownPromise,
        IPlaybackRunner? parentRunner,
        ref TStateMachine initialStateMachine
    )
    {
        this.playback = playback;
        OwnPromise = ownPromise;
        ParentRunner = parentRunner;

        initialSnapshot = SnapshotStateMachine(initialStateMachine);
        current = SnapshotStateMachine(initialSnapshot);
    }

    public long Epoch { get; private set; }

    public PlaybackPromiseBase OwnPromise { get; }

    public IPlaybackRunner? ParentRunner { get; }
    public int? CurrentCallRecordIndex { get; private set; }

    public int ParentAwaitCheckpointId { get; private set; }
    public int CurrentStateCheckpointId { get; private set; }

    public TimelineRecord this[int recordIndex] => playback.GetRecord(recordIndex);

    public int? GetResumeScope(int checkpointId)
    {
        if (checkpointId <= 0 || checkpointId > checkpointScopes.Length)
            return null;
        return checkpointScopes[checkpointId - 1];
    }

    public void BindCurrentCallRecord(int callRecordIndex)
    {
        CurrentCallRecordIndex = callRecordIndex;
    }

    public void SetParentAwaitCheckpointId(int checkpointId)
    {
        ParentAwaitCheckpointId = checkpointId;
        if (CurrentCallRecordIndex is { } callRecordIndex)
            playback.BindCallParentAwaitCheckpoint(callRecordIndex, checkpointId);
    }

    public void MoveNext()
    {
        if (isCompleted)
            return;

        PlaybackRuntime.PushPlayback(playback);
        PlaybackRuntime.PushRunner(this);

        var pushedScope = false;
        var scope = scopeForNextMoveNext;

        if (scope is { } scopeIndex)
        {
            PlaybackRuntime.PushRecordScope(scopeIndex);
            pushedScope = true;
        }

        scopeForNextMoveNext = null;

        try
        {
            if (!isCompleted)
                current.MoveNext();
        }
        finally
        {
            if (pushedScope)
                PlaybackRuntime.PopRecordScope(scope!.Value);

            PlaybackRuntime.PopRunner(this);
            PlaybackRuntime.PopPlayback(playback);
        }
    }

    public void ResumeFromAwait(int checkpointId, long expectedEpoch, int? resumeScopeIndex)
    {
        if (isCompleted)
            return;

        if (expectedEpoch != Epoch)
            return;

        if (checkpointId != suspendedCheckpointId)
            return;
        unchecked
        {
            Epoch++;
        }

        suspendedCheckpointId = 0;
        CurrentStateCheckpointId = checkpointId;
        scopeForNextMoveNext = resumeScopeIndex;

        MoveNext();
    }

    public void RestoreCheckpoint(int checkpointId)
    {
        if (checkpointId == 0)
        {
            RestoreInitialCheckpoint();
            return;
        }

        if (checkpointId <= 0 || checkpointId > checkpoints.Length)
            throw new InvalidOperationException($"Checkpoint {checkpointId} does not exist.");

        current = SnapshotStateMachine(checkpoints[checkpointId - 1]);
        suspendedCheckpointId = checkpointId;
        isCompleted = false;

        OwnPromise.ResetForReplay();
        unchecked
        {
            Epoch++;
        }
    }

    public void RestoreInitialCheckpoint()
    {
        // Preserve checkpoint dictionaries. Timeline records keep EntryCheckpoint
        // references into these snapshots; clearing them makes old records
        // unrestorable and can cause recursive replay attempts.
        suspendedCheckpointId = 0;
        CurrentStateCheckpointId = 0;
        isCompleted = false;
        scopeForNextMoveNext = null;

        // Do not clear _currentCallRecord. For child method entry checkpoints,
        // the current call record is the scope in which the child body records
        // should be recreated/consumed. Root runners naturally have null here.

        current = SnapshotStateMachine(initialSnapshot);
        OwnPromise.ResetForReplay();
        unchecked
        {
            Epoch++;
        }
    }

    public void MarkCompleted()
    {
        isCompleted = true;
        suspendedCheckpointId = 0;
        unchecked
        {
            Epoch++;
        }
    }

    public int CaptureReplaySuspension(
        ref TStateMachine stateMachine,
        int ownerRecordIndex,
        int? resumeScopeIndex
    )
    {
        var checkpointId = CaptureCheckpointCore(ref stateMachine, resumeScopeIndex);
        suspendedCheckpointId = checkpointId;
        return checkpointId;
    }

    public int CaptureCheckpoint(
        ref TStateMachine stateMachine,
        Playback.IPlaybackAwaitBehavior awaitBehavior,
        PlaybackPromiseBase? awaitedPromise,
        int? ownerRecordIndex,
        int? resumeScopeIndex
    )
    {
        var checkpointId = CaptureCheckpointCore(ref stateMachine, resumeScopeIndex);

        playback.OnCheckpointCaptured(
            this,
            checkpointId,
            awaitBehavior,
            awaitedPromise,
            ownerRecordIndex,
            resumeScopeIndex
        );

        return checkpointId;
    }

    private int CaptureCheckpointCore(ref TStateMachine stateMachine, int? resumeScopeIndex)
    {
        var storedSnapshot = SnapshotStateMachine(stateMachine);
        var workingCopy = SnapshotStateMachine(storedSnapshot);

        current = workingCopy;

        var checkpointId = ++nextCheckpointId;
        if (checkpointId > checkpoints.Length)
        {
            Array.Resize(ref checkpoints, Math.Max(2, checkpoints.Length * 2));
            Array.Resize(ref checkpointScopes, Math.Max(2, checkpointScopes.Length * 2));
        }
        checkpoints[checkpointId - 1] = storedSnapshot;
        checkpointScopes[checkpointId - 1] = resumeScopeIndex;
        suspendedCheckpointId = checkpointId;

        return checkpointId;
    }

    private static TStateMachine SnapshotStateMachine(TStateMachine source)
    {
        if (typeof(TStateMachine).IsValueType)
            return source;

        if (source == null)
            throw new InvalidOperationException("State machine is null.");

        return CloneUtility.Clone(source);
    }
}
