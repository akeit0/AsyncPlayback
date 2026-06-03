using System.Reflection;
using System.Runtime.CompilerServices;

namespace AsyncPlayback;

internal interface IPlaybackRunner
{
    long Epoch { get; }
    PlaybackPromiseBase OwnPromise { get; }
    IPlaybackRunner? ParentRunner { get; }
    CallTimelineRecord? CurrentCallRecord { get; }
    int ParentAwaitCheckpointId { get; }

    TimelineRecord? GetResumeScope(int checkpointId);

    void BindCurrentCallRecord(CallTimelineRecord call);
    void SetParentAwaitCheckpointId(int checkpointId);

    void MoveNext();
    void ResumeFromAwait(int checkpointId, long expectedEpoch, TimelineRecord? resumeScope);

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
    private TimelineRecord?[] checkpointScopes = [];
    private readonly TStateMachine initialSnapshot;

    private readonly Playback playback;

    private TStateMachine current;
    private bool isCompleted;
    private int nextCheckpointId;
    private TimelineRecord? scopeForNextMoveNext;
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
    public CallTimelineRecord? CurrentCallRecord { get; private set; }

    public int ParentAwaitCheckpointId { get; private set; }

    public TimelineRecord? GetResumeScope(int checkpointId)
    {
        if (checkpointId <= 0 || checkpointId > checkpointScopes.Length)
            return null;
        return checkpointScopes[checkpointId - 1];
    }

    public void BindCurrentCallRecord(CallTimelineRecord call)
    {
        CurrentCallRecord = call ?? throw new ArgumentNullException(nameof(call));
    }

    public void SetParentAwaitCheckpointId(int checkpointId)
    {
        ParentAwaitCheckpointId = checkpointId;
        CurrentCallRecord?.BindParentAwaitCheckpoint(checkpointId);
    }

    public void MoveNext()
    {
        if (isCompleted)
            return;

        PlaybackRuntime.Pushplayback(playback);
        PlaybackRuntime.PushRunner(this);

        var pushedScope = false;
        var scope = scopeForNextMoveNext;

        if (scope != null)
        {
            PlaybackRuntime.PushRecordScope(scope);
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
                PlaybackRuntime.PopRecordScope(scope!);

            PlaybackRuntime.PopRunner(this);
            PlaybackRuntime.Popplayback(playback);
        }
    }

    public void ResumeFromAwait(int checkpointId, long expectedEpoch, TimelineRecord? resumeScope)
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
        scopeForNextMoveNext = resumeScope;

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

    public int CaptureCheckpoint(
        ref TStateMachine stateMachine,
        PlaybackPromiseKind awaitKind,
        PlaybackPromiseBase? awaitedPromise,
        TimelineRecord? ownerRecord,
        TimelineRecord? resumeScope
    )
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
        checkpointScopes[checkpointId - 1] = resumeScope;
        suspendedCheckpointId = checkpointId;

        playback.OnCheckpointCaptured(
            this,
            checkpointId,
            awaitKind,
            awaitedPromise,
            ownerRecord,
            resumeScope
        );

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
