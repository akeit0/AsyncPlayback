namespace AsyncPlayback;

public sealed partial class Playback
{
    private static readonly Action<PlaybackPromise> CompletePromise = static promise =>
        promise.TrySetResult();

    private static readonly Action<PlaybackPromise<bool>> CompleteBoolPromiseWithFalse =
        static promise => promise.TrySetResult(false);

    private static readonly Action<PostedDelayCompletion> CompleteDelay = static completion =>
        completion.Playback.recordRuntime.CompleteDelay(completion.RecordIndex);

    private static readonly Action<IPlaybackRunner> MoveRunnerNext = static runner =>
        runner.MoveNext();

    private static readonly Action<PostedCheckpointResume> ResumeCheckpoint = static resume =>
    {
        resume.Runner.ResumeFromAwait(
            resume.CheckpointId,
            resume.ExpectedEpoch,
            resume.ResumeScope
        );
    };

    private static readonly Action<PostedEffectFailure> FailPostedEffect = static failure =>
    {
        failure.Playback.FailEffect(
            failure.RecordId,
            failure.Promise,
            failure.StartTimestamp,
            failure.EndTimestamp,
            failure.Direction,
            failure.Exception
        );
    };

    private readonly List<int> activeSeekLoopIndexes = [];
    private readonly PlaybackScheduler scheduler = new();
    private readonly PlaybackStateStore stateStore = new();
    private readonly TimelineRecordRuntime recordRuntime = new();
    private readonly Timeline timeline;
    private bool hasTimestamp;
    private int suppressAwaitPointTimestampSamplingDepth;
    private long timestamp;
    private long checkpointSequence;
    private int? currentRecordIndex;
    private int? pendingForwardCheckpointIndex;
    private BoundaryCursor? boundaryCursor;
    private int nextRecordId;
    private int playbackRecordIndex;
    private PlaybackEdgeCursor edgeCursor;
    private CancellationToken currentCancellationToken;
    private PlaybackDirection currentDirection = PlaybackDirection.Forward;
    private TimeSpan transportStartTime;
    private readonly HashSet<PlaybackEventBoundaryKey> emittedBoundaries = [];

    private IPlaybackRunner? rootRunner;
    private bool suppressImplicitCallContinuationBoundary;
    private int suppressCheckpointAutoContinuationDepth;
    private int? suppressLoopExitForIndex;

    public static Playback? Current => PlaybackRuntime.CurrentPlayback;

    public TimeSpan TargetTime { get; private set; }
    public TimeSpan Time { get; private set; }

    public PlaybackDirection CurrentDirection => currentDirection;
    public TimeProvider TimeProvider { get; }
    public long Timestamp => timestamp;
    public TimeSpan DeltaTime { get; private set; }
    public bool DebugLogging { get; set; }
    public PlaybackMode Mode { get; private set; } = PlaybackMode.Recording;

    public bool IsStarted { get; private set; }

    public bool IsCompleted { get; private set; }

    internal TimeSpan TransportStartTime => transportStartTime;

    internal CancellationToken CurrentCancellationToken => currentCancellationToken;

    public event Action<PlaybackEvent>? EventOccurred;

    public IReadOnlyList<TimelineRecordInfo> Records => timeline.ToInfos();
    public TimelineRecordInfo? CurrentRecord =>
        currentRecordIndex is { } index ? GetRecord(index).ToInfo() : null;

    public CheckpointAwaitable Checkpoint(string debugLabel = "Checkpoint") =>
        new(this, debugLabel);

    public void Store<T>(T state)
        where T : notnull
    {
        EnsureStarted();

        stateStore.Store(state);
        stateStore.CaptureAtCurrentRunner();
    }

    public void ClearStore()
    {
        EnsureStarted();

        stateStore.Clear();
        stateStore.CaptureAtCurrentRunner();
    }

    private void StoreAtCurrentStateIfMissing<T>(T state)
        where T : notnull
    {
        stateStore.StoreAtCurrentRunnerIfMissing(state);
    }

    private void BindStoreCheckpoint(TimelineCheckpoint checkpoint)
    {
        stateStore.BindNewSlot(checkpoint, CaptureStoreSnapshot());
    }

    public bool TryGet<T>(out T state)
    {
        EnsureStarted();
        return stateStore.TryGet(out state);
    }

    public T SelectByDirection<T>(T backwardStore, T forward)
        where T : notnull
    {
        EnsureStarted();

        if (CurrentDirection == PlaybackDirection.Backward)
        {
            if (TryGet<T>(out var state))
                return state;
            throw new InvalidOperationException("No stored state found for backward selection.");
        }

        StoreAtCurrentStateIfMissing(backwardStore);
        return forward;
    }

    public IReadOnlyList<TimelineRecordInfo> GetNearestRecords(int count = 5)
    {
        if (count <= 0)
            return [];

        var currentIndex = currentRecordIndex ?? -1;

        return timeline.GetNearestRecords(Time, count, currentIndex);
    }

    public IReadOnlyList<string> GetNearestDebugLabels(int count = 5)
    {
        return GetNearestRecords(count).Select(static record => record.DebugLabel).ToArray();
    }

    internal bool SuppressCheckpointAutoContinuation => suppressCheckpointAutoContinuationDepth > 0;

    public Playback(TimeProvider? timeProvider = null)
    {
        timeline = new(this);
        TimeProvider = timeProvider ?? global::System.TimeProvider.System;
        ResetTimestamp();
    }

    public static Playback Start(
        Func<Playback, PlaybackTask> entry,
        TimeProvider? timeProvider = null
    )
    {
        var playback = new Playback(timeProvider);
        playback.Start(entry);
        return playback;
    }

    public void Start(Func<Playback, PlaybackTask> entry)
    {
        if (IsStarted)
            throw new InvalidOperationException("This playback has already been started.");

        if (entry == null)
            throw new ArgumentNullException(nameof(entry));

        IsStarted = true;
        IsCompleted = false;
        Time = TimeSpan.Zero;
        Mode = PlaybackMode.Recording;
        playbackRecordIndex = 0;
        edgeCursor = PlaybackEdgeCursor.None;
        stateStore.Reset();
        ResetTimestamp();

        PlaybackRuntime.PushPlayback(this);
        try
        {
            _ = entry(this);
        }
        finally
        {
            PlaybackRuntime.PopPlayback(this);
        }
    }

    public PlaybackTask Yield()
    {
        EnsureCurrentPlayback();

        var promise = new PlaybackPromise(this, YieldAwaitBehavior.Instance)
        {
            StartTime = Time,
            Duration = TimeSpan.Zero,
        };

        Post(promise, CompletePromise);
        return new(promise);
    }

    public PlaybackTask Delay(TimeSpan duration, string debugLabel = "Delay")
    {
        var playback = this;

        var record = playback.UseDelayRecord(duration, debugLabel);
        if (currentDirection == PlaybackDirection.Backward)
            return PlaybackTask.SuspendReplayAt(record.FlatIndex);

        var promise = new PlaybackPromise(playback, DelayAwaitBehavior.Instance)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            OwnerRecordIndex = record.FlatIndex,
        };

        recordRuntime.ArmDelay(record.FlatIndex, promise);

        if (record.Duration == TimeSpan.Zero)
            playback.Post(new PostedDelayCompletion(playback, record.FlatIndex), CompleteDelay);

        return new(promise);
    }

    public PlaybackTask Effect(
        Func<CancellationToken, ValueTask> effect,
        string debugLabel = "Effect"
    )
    {
        return Effect(effect, revert: null, debugLabel);
    }

    public PlaybackTask Effect(
        Func<CancellationToken, ValueTask> effect,
        Func<CancellationToken, ValueTask>? revert,
        string debugLabel = "Effect"
    )
    {
        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        EnsureCurrentPlayback();

        var record = UseEffectRecord(debugLabel, effect);

        if (currentDirection == PlaybackDirection.Backward)
        {
            if (revert != null)
            {
                var revertFailure = StartReplayRevertEffect(revert);
                if (revertFailure != null)
                    return PlaybackTask.FromException(this, revertFailure);
            }

            return PlaybackTask.SuspendReplayAt(record.FlatIndex);
        }

        var promise = new PlaybackPromise(this, EffectAwaitBehavior.Instance)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            OwnerRecordIndex = record.FlatIndex,
        };

        StartEffect(record.Id, promise);
        return new(promise);
    }

    public PlaybackTask<T> Effect<T>(
        Func<CancellationToken, ValueTask<T>> effect,
        string debugLabel = "Effect"
    )
    {
        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        EnsureCurrentPlayback();

        var record = UseEffectRecord(debugLabel, effect);

        if (currentDirection == PlaybackDirection.Backward)
        {
            return PlaybackTask<T>.SuspendReplayAt(record.FlatIndex);
        }

        var promise = new PlaybackPromise<T>(this, EffectAwaitBehavior.Instance)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            OwnerRecordIndex = record.FlatIndex,
        };

        StartEffect(record.Id, promise);
        return new(promise);
    }

    public SeekLoopEnumerable ForEachOnSeek(TimeSpan duration, string debugLabel = "ForEachOnSeek")
    {
        EnsureCurrentPlayback();

        var record = UseSeekLoopRecord(duration, debugLabel);
        return new(this, record.FlatIndex);
    }

    internal TimelineRecord GetSeekLoopRecord(int recordIndex)
    {
        var record = GetRecord(recordIndex);
        return record.Behavior is TimelineRecordBehavior builtIn && builtIn.IsSeekLoopRecord(record)
            ? record
            : throw new InvalidOperationException("Record index does not refer to a seek loop.");
    }

    internal PlaybackTask<bool> ArmSeekLoopMoveNext(int recordIndex)
    {
        var record = GetSeekLoopRecord(recordIndex);

        var suppressExit =
            Time >= record.EndTime
            && recordRuntime.HasDeliveredFinalSeekLoopTrue(record.FlatIndex)
            && suppressLoopExitForIndex == record.FlatIndex;

        var isExitMoveNext =
            Time >= record.EndTime
            && recordRuntime.HasDeliveredFinalSeekLoopTrue(record.FlatIndex)
            && !suppressExit;

        var ownerRecord = isExitMoveNext
            ? UseImplicitCheckpointRecord($"exit {record.DebugLabel}")
            : (TimelineRecord)record;

        if (currentDirection == PlaybackDirection.Backward)
            return PlaybackTask<bool>.SuspendReplayAt(ownerRecord.FlatIndex);

        var promise = new PlaybackPromise<bool>(this, SeekLoopMoveNextAwaitBehavior.Instance)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            OwnerRecordIndex = ownerRecord.FlatIndex,
        };

        if (isExitMoveNext)
        {
            // This is the compiler-generated MoveNextAsync() after the final
            // true sample. It represents the continuation after await foreach.
            // The promise must complete with false, but the checkpoint belongs
            // to the implicit exit checkpoint record rather than the loop body.
            Post(promise, CompleteBoolPromiseWithFalse);
            return new(promise);
        }

        recordRuntime.ArmSeekLoopMoveNext(record.FlatIndex, promise);

        if (!activeSeekLoopIndexes.Contains(record.FlatIndex))
            activeSeekLoopIndexes.Add(record.FlatIndex);

        if (suppressExit)
            suppressLoopExitForIndex = null;

        return new(promise);
    }

    internal TimeSpan GetSeekLoopElapsed(int recordIndex)
    {
        return recordRuntime.GetSeekLoopElapsed(recordIndex);
    }

    internal TimelineRecord GetRecord(int recordIndex)
    {
        return timeline.Get(recordIndex);
    }

    private ref TimelineRecord GetRecordRef(int recordIndex)
    {
        return ref timeline.GetRef(recordIndex);
    }

    private TimelineRecord GetRecord(RecordId recordId)
    {
        return GetRecord(GetRecordIndex(recordId));
    }

    internal int GetRecordIndex(RecordId recordId)
    {
        return timeline.GetIndex(recordId);
    }

    private TimelineRecord? GetCurrentRecord()
    {
        return currentRecordIndex is { } index ? GetRecord(index) : null;
    }

    internal void SetCurrentRecord(RecordId recordId)
    {
        currentRecordIndex = GetRecordIndex(recordId);
    }

    private void SetCurrentRecord(int? recordIndex)
    {
        currentRecordIndex = recordIndex;
    }

    private readonly record struct BoundaryCursor(
        PlaybackDirection Direction,
        TimelineBoundary Boundary
    );

    private readonly record struct PlaybackEventBoundaryKey(
        RecordId RecordId,
        PlaybackBoundaryKind BoundaryKind,
        PlaybackDirection Direction,
        TimeSpan Time
    );

    private sealed record PostedCheckpointResume(
        IPlaybackRunner Runner,
        int CheckpointId,
        long ExpectedEpoch,
        int? ResumeScope
    );

    private sealed record PostedDelayCompletion(Playback Playback, int RecordIndex);

    private sealed record PostedEffectFailure(
        Playback Playback,
        RecordId RecordId,
        PlaybackPromiseBase Promise,
        long StartTimestamp,
        long EndTimestamp,
        PlaybackDirection Direction,
        Exception Exception
    );

    private enum PlaybackEdgeCursor
    {
        None,
        BeforeFirst,
        AfterLast,
    }

    private readonly record struct InitialEdgeResult(bool Moved, AwaitPoint AwaitPoint);

    private readonly record struct AwaitPoint(bool Stopped, TimelineBoundary? Boundary);

    private CancellationTokenScope PushCancellationToken(CancellationToken cancellationToken)
    {
        var previous = currentCancellationToken;
        currentCancellationToken = cancellationToken;
        return new(this, previous);
    }

    private readonly struct CancellationTokenScope : IDisposable
    {
        private readonly Playback playback;
        private readonly CancellationToken previous;

        public CancellationTokenScope(Playback playback, CancellationToken previous)
        {
            this.playback = playback;
            this.previous = previous;
        }

        public void Dispose()
        {
            playback.currentCancellationToken = previous;
        }
    }

    private void EnsureStarted()
    {
        if (!IsStarted)
            throw new InvalidOperationException("playback has not been started.");
    }

    private void EnsureCurrentPlayback()
    {
        EnsureStarted();

        if (!ReferenceEquals(PlaybackRuntime.CurrentPlayback, this))
            throw new InvalidOperationException(
                "playback awaitables must be created while this playback is active. "
                    + "Start the root method with playback.Start(() => Scenario(playback))."
            );
    }
}
