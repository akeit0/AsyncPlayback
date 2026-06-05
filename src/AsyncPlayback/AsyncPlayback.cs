namespace AsyncPlayback;

public enum TransportEvaluation
{
    TargetOnly,
    Traverse,
}

public readonly record struct TransportOptions(TransportEvaluation Evaluation, bool EvaluateTarget)
{
    public static TransportOptions TargetOnly { get; } = new(TransportEvaluation.TargetOnly, true);

    public static TransportOptions Traverse { get; } = new(TransportEvaluation.Traverse, true);
}

public readonly record struct StepResult(
    bool Moved,
    TimeSpan Time,
    long Timestamp,
    TimeSpan DeltaTime = default,
    TimelineRecordInfo? Record = null,
    PlaybackBoundaryKind? BoundaryKind = null
)
{
    public string? DebugLabel => Record?.DebugLabel;
}

public sealed class Playback
{
    private readonly List<SeekLoopRecord> activeSeekLoops = [];
    private readonly object readyGate = new();
    private readonly Queue<Action> ready = [];
    private readonly SemaphoreSlim readySignal = new(0);
    private readonly List<TimelineRecord> records = [];
    private readonly Dictionary<RunnerStateKey, TimelineCheckpoint> storeCheckpoints = [];
    private bool hasTimestamp;
    private int suppressAwaitPointTimestampSamplingDepth;
    private object? storedState;
    private long timestamp;
    private long checkpointSequence;
    private TimelineRecord? currentRecord;
    private CheckpointTimelineRecord? pendingForwardCheckpoint;
    private long generation;
    private BoundaryCursor? boundaryCursor;
    private int nextRecordId;
    private int pendingExternalEffects;
    private int playbackRecordIndex;
    private bool hasStoredState;
    private PlaybackEdgeCursor edgeCursor;
    private CancellationToken currentCancellationToken;
    private PlaybackDirection currentDirection = PlaybackDirection.Forward;

    private IPlaybackRunner? rootRunner;
    private bool suppressImplicitCallContinuationBoundary;
    private int suppressCheckpointAutoContinuationDepth;
    private SeekLoopRecord? suppressLoopExitFor;

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

    public IReadOnlyList<TimelineRecordInfo> Records => GetRecordInfos();
    public TimelineRecordInfo? CurrentRecord => currentRecord?.ToInfo();

    public CheckpointAwaitable Checkpoint(string debugLabel = "Checkpoint") =>
        new(this, debugLabel);

    public void Store<T>(T state)
        where T : notnull
    {
        EnsureStarted();

        storedState = state;
        hasStoredState = true;

        CaptureStoreAtCurrentState();
    }

    public void ClearStore()
    {
        EnsureStarted();

        ClearStoredState();
        CaptureStoreAtCurrentState();
    }

    private void CaptureStoreAtCurrentState()
    {
        if (TryGetCurrentStoreCheckpoint(out var checkpoint))
            checkpoint.ResumeStoreSnapshot = CaptureStoreSnapshot();
    }

    private bool TryGetCurrentStoreCheckpoint(out TimelineCheckpoint checkpoint)
    {
        var runner = PlaybackRuntime.CurrentRunner;
        if (runner == null)
        {
            checkpoint = null!;
            return false;
        }

        return storeCheckpoints.TryGetValue(
            new(runner, runner.CurrentStateCheckpointId),
            out checkpoint!
        );
    }

    private void StoreAtCurrentStateIfMissing<T>(T state)
        where T : notnull
    {
        var hasCheckpoint = TryGetCurrentStoreCheckpoint(out var checkpoint);
        if (hasCheckpoint && checkpoint.ResumeStoreSnapshot != null)
        {
            RestoreStoreSnapshot(checkpoint.ResumeStoreSnapshot);
            return;
        }

        storedState = state;
        hasStoredState = true;

        if (hasCheckpoint)
            checkpoint.ResumeStoreSnapshot = CaptureStoreSnapshot();
    }

    private void BindStoreCheckpoint(TimelineCheckpoint checkpoint)
    {
        BindStoreCheckpoint(checkpoint.Runner, checkpoint.CheckpointId, checkpoint);
    }

    private void BindStoreCheckpoint(
        IPlaybackRunner runner,
        int checkpointId,
        TimelineCheckpoint checkpoint
    )
    {
        storeCheckpoints[new(runner, checkpointId)] = checkpoint;
    }

    public bool TryGet<T>(out T state)
    {
        EnsureStarted();

        if (!hasStoredState)
        {
            state = default!;
            return false;
        }

        if (storedState == null)
        {
            state = default!;
            return default(T) is null;
        }

        if (storedState is not T value)
        {
            state = default!;
            return false;
        }

        state = value;
        return true;
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

        var currentIndex = currentRecord?.FlatIndex ?? -1;

        return records
            .OrderBy(record => AbsTicks(record.StartTime - Time))
            .ThenBy(record =>
                currentIndex < 0 ? record.FlatIndex : Math.Abs(record.FlatIndex - currentIndex)
            )
            .ThenBy(record => record.FlatIndex)
            .Take(count)
            .Select(static record => record.ToInfo())
            .ToArray();
    }

    public IReadOnlyList<string> GetNearestDebugLabels(int count = 5)
    {
        return GetNearestRecords(count).Select(static record => record.DebugLabel).ToArray();
    }

    internal bool SuppressCheckpointAutoContinuation => suppressCheckpointAutoContinuationDepth > 0;

    public Playback(TimeProvider? timeProvider = null)
    {
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
        storeCheckpoints.Clear();
        ResetTimestamp();
        ClearStoredState();

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

        var promise = new PlaybackPromise(this, PlaybackPromiseKind.Yield)
        {
            StartTime = Time,
            Duration = TimeSpan.Zero,
            DueTime = Time,
        };

        Post(() => promise.TrySetResult());
        return new(promise);
    }

    public PlaybackTask Delay(TimeSpan duration, string debugLabel = "Delay")
    {
        var playback = this;

        var record = playback.GetOrCreateDelayRecord(duration, debugLabel);
        var promise = new PlaybackPromise(playback, PlaybackPromiseKind.Delay)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            DueTime = record.EndTime,
            OwnerRecord = record,
        };

        record.ArmDelay(promise);

        if (record.Duration == TimeSpan.Zero)
            playback.Post(() => record.Complete());

        return new(promise);
    }

    public PlaybackTask Effect(Func<ValueTask> effect, string debugLabel = "Effect")
    {
        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        return Effect(_ => effect(), debugLabel);
    }

    public PlaybackTask Effect(
        Func<CancellationToken, ValueTask> effect,
        string debugLabel = "Effect"
    )
    {
        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        EnsureCurrentPlayback();

        var record = GetOrCreateEffectRecord(
            debugLabel,
            async cancellationToken =>
            {
                await effect(cancellationToken).ConfigureAwait(false);
                return null;
            }
        );

        var promise = new PlaybackPromise(this, PlaybackPromiseKind.Effect)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            DueTime = record.EndTime,
            OwnerRecord = record,
        };

        StartEffect(record, promise);
        return new(promise);
    }

    public PlaybackTask<T> Effect<T>(Func<ValueTask<T>> effect, string debugLabel = "Effect")
    {
        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        return Effect(_ => effect(), debugLabel);
    }

    public PlaybackTask<T> Effect<T>(
        Func<CancellationToken, ValueTask<T>> effect,
        string debugLabel = "Effect"
    )
    {
        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        EnsureCurrentPlayback();

        var record = GetOrCreateEffectRecord(
            debugLabel,
            async cancellationToken => await effect(cancellationToken).ConfigureAwait(false)
        );

        var promise = new PlaybackPromise<T>(this, PlaybackPromiseKind.Effect)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            DueTime = record.EndTime,
            OwnerRecord = record,
        };

        StartEffect(record, promise);
        return new(promise);
    }

    public SeekLoopEnumerable ForEachOnSeek(TimeSpan duration, string debugLabel = "ForEachOnSeek")
    {
        EnsureCurrentPlayback();

        var record = GetOrCreateSeekLoopRecord(duration, debugLabel);
        return new(this, record);
    }

    internal PlaybackTask<bool> ArmSeekLoopMoveNext(SeekLoopRecord record)
    {
        if (record == null)
            throw new ArgumentNullException(nameof(record));

        var suppressExit =
            Time >= record.EndTime
            && record.FinalTrueDelivered
            && ReferenceEquals(suppressLoopExitFor, record);

        var isExitMoveNext = Time >= record.EndTime && record.FinalTrueDelivered && !suppressExit;

        var ownerRecord = isExitMoveNext
            ? GetOrCreateImplicitCheckpointRecord($"exit {record.DebugLabel}")
            : (TimelineRecord)record;

        var promise = new PlaybackPromise<bool>(this, PlaybackPromiseKind.SeekLoopMoveNext)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            DueTime = record.EndTime,
            OwnerRecord = ownerRecord,
        };

        if (isExitMoveNext)
        {
            // This is the compiler-generated MoveNextAsync() after the final
            // true sample. It represents the continuation after await foreach.
            // The promise must complete with false, but the checkpoint belongs
            // to the implicit exit checkpoint record rather than the loop body.
            Post(() => promise.TrySetResult(false));
            return new(promise);
        }

        record.ArmMoveNext(promise);

        if (!activeSeekLoops.Contains(record))
            activeSeekLoops.Add(record);

        if (suppressExit)
            suppressLoopExitFor = null;

        return new(promise);
    }

    public ValueTask MoveByAsync(
        TimeSpan virtualDelta,
        CancellationToken cancellationToken = default
    )
    {
        return MoveByAsync(virtualDelta, TransportOptions.Traverse, cancellationToken);
    }

    public ValueTask MoveByAsync(
        TimeSpan virtualDelta,
        TransportOptions options,
        CancellationToken cancellationToken = default
    )
    {
        var target = Time + virtualDelta;

        if (target < TimeSpan.Zero)
            target = TimeSpan.Zero;

        var direction =
            virtualDelta < TimeSpan.Zero ? PlaybackDirection.Backward
            : virtualDelta > TimeSpan.Zero ? PlaybackDirection.Forward
            : (PlaybackDirection?)null;

        return TransportToAsync(target, options, direction, cancellationToken);
    }

    public ValueTask AdvanceByElapsedTimeAsync(CancellationToken cancellationToken = default)
    {
        return AdvanceByElapsedTimeAsync(TransportOptions.Traverse, cancellationToken);
    }

    public async ValueTask AdvanceByElapsedTimeAsync(
        TransportOptions options,
        CancellationToken cancellationToken = default
    )
    {
        SampleTimestamp();
        using var timestampScope = SuppressAwaitPointTimestampSampling();
        await MoveByAsync(DeltaTime, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask RewindByElapsedTimeAsync(CancellationToken cancellationToken = default)
    {
        return RewindByElapsedTimeAsync(TransportOptions.Traverse, cancellationToken);
    }

    public async ValueTask RewindByElapsedTimeAsync(
        TransportOptions options,
        CancellationToken cancellationToken = default
    )
    {
        SampleTimestamp();
        using var timestampScope = SuppressAwaitPointTimestampSampling();
        await MoveByAsync(-DeltaTime, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask MoveToAsync(TimeSpan targetTime, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(targetTime, TransportOptions.Traverse, cancellationToken);
    }

    public ValueTask MoveToAsync(
        TimeSpan targetTime,
        TransportOptions options,
        CancellationToken cancellationToken = default
    )
    {
        return TransportToAsync(targetTime, options, null, cancellationToken);
    }

    public ValueTask MoveToAsync(
        TimeSpan targetTime,
        PlaybackDirection direction,
        CancellationToken cancellationToken = default
    )
    {
        return MoveToAsync(targetTime, direction, TransportOptions.Traverse, cancellationToken);
    }

    public ValueTask MoveToAsync(
        TimeSpan targetTime,
        PlaybackDirection direction,
        TransportOptions options,
        CancellationToken cancellationToken = default
    )
    {
        return TransportToAsync(targetTime, options, direction, cancellationToken);
    }

    public ValueTask SeekToAsync(TimeSpan targetTime, CancellationToken cancellationToken = default)
    {
        return SeekToAsync(targetTime, TransportOptions.TargetOnly, cancellationToken);
    }

    public ValueTask SeekToAsync(
        TimeSpan targetTime,
        TransportOptions options,
        CancellationToken cancellationToken = default
    )
    {
        return TransportToAsync(targetTime, options, null, cancellationToken);
    }

    public ValueTask<StepResult> TryStepForwardAsync(CancellationToken cancellationToken = default)
    {
        return TryStepForwardAsync(PlaybackStepGranularity.AwaitPoint, cancellationToken);
    }

    public ValueTask<StepResult> TryStepForwardAsync(
        PlaybackStepGranularity granularity,
        CancellationToken cancellationToken = default
    )
    {
        return TryStepAsync(PlaybackDirection.Forward, granularity, cancellationToken);
    }

    public ValueTask<StepResult> TryStepBackAsync(CancellationToken cancellationToken = default)
    {
        return TryStepBackAsync(PlaybackStepGranularity.AwaitPoint, cancellationToken);
    }

    public ValueTask<StepResult> TryStepBackAsync(
        PlaybackStepGranularity granularity,
        CancellationToken cancellationToken = default
    )
    {
        return TryStepAsync(PlaybackDirection.Backward, granularity, cancellationToken);
    }

    private async ValueTask<StepResult> TryStepAsync(
        PlaybackDirection direction,
        PlaybackStepGranularity granularity,
        CancellationToken cancellationToken
    )
    {
        EnsureStarted();
        currentDirection = direction;
        using var cancellationScope = PushCancellationToken(cancellationToken);

        if (direction == PlaybackDirection.Forward && edgeCursor == PlaybackEdgeCursor.AfterLast)
            return CreateStepResult(false, null);

        AwaitPoint capturedAwaitPoint = default;
        var initialEdgeEvaluated = false;
        var terminalEdgeEvaluated = false;

        if (direction == PlaybackDirection.Forward)
        {
            var initialEdge = await TryEvaluateInitialForwardEdgeAsync(
                    direction,
                    granularity,
                    cancellationToken
                )
                .ConfigureAwait(false);

            initialEdgeEvaluated = initialEdge.Moved;
            capturedAwaitPoint = initialEdge.AwaitPoint;
        }

        if (direction == PlaybackDirection.Forward && !capturedAwaitPoint.Stopped)
            capturedAwaitPoint = await RunUntilNextAwaitPointAsync(
                    direction,
                    granularity,
                    cancellationToken
                )
                .ConfigureAwait(false);

        if (capturedAwaitPoint.Stopped)
        {
            if (
                capturedAwaitPoint.Boundary is
                { Record: SeekLoopRecord, Kind: TimelineBoundaryKind.Start } seekStart
            )
                await EvaluateStepBoundaryAsync(seekStart, direction).ConfigureAwait(false);

            return CreateStepResult(true, capturedAwaitPoint.Boundary);
        }

        if (
            direction == PlaybackDirection.Forward
            && !HasReady()
            && pendingForwardCheckpoint is { EntryCheckpoint: not null } checkpoint
        )
        {
            pendingForwardCheckpoint = null;
            RestoreRunnerTreeTo(checkpoint.EntryCheckpoint, true);

            capturedAwaitPoint = await RunUntilNextAwaitPointAsync(
                    direction,
                    granularity,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (capturedAwaitPoint.Stopped)
            {
                if (
                    capturedAwaitPoint.Boundary is
                    { Record: SeekLoopRecord, Kind: TimelineBoundaryKind.Start } seekStart
                )
                    await EvaluateStepBoundaryAsync(seekStart, direction).ConfigureAwait(false);

                return CreateStepResult(true, capturedAwaitPoint.Boundary);
            }
        }

        if (direction == PlaybackDirection.Backward)
            terminalEdgeEvaluated = await TryEvaluateTerminalBackwardEdgeAsync()
                .ConfigureAwait(false);

        var boundary = FindStepBoundary(direction, granularity);
        if (boundary == null)
        {
            if (direction == PlaybackDirection.Backward && Time == TimeSpan.Zero)
                RestoreToInitial(postReady: false);

            return CreateStepResult(initialEdgeEvaluated || terminalEdgeEvaluated, null);
        }

        await EvaluateStepBoundaryAsync(boundary.Value, direction).ConfigureAwait(false);

        boundaryCursor = new(direction, boundary.Value);
        return CreateStepResult(true, boundary);
    }

    public async ValueTask RunToEndAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();

        while ((await TryStepForwardAsync(cancellationToken).ConfigureAwait(false)).Moved) { }
    }

    public async ValueTask RunBackToStartAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();

        while ((await TryStepBackAsync(cancellationToken).ConfigureAwait(false)).Moved) { }
    }

    private async ValueTask RunReadyAsync(CancellationToken cancellationToken = default)
    {
        while (TryRunOneReady())
            await Task.Yield();
    }

    private async ValueTask RunUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            while (TryRunOneReady())
                await Task.Yield();

            if (Volatile.Read(ref pendingExternalEffects) == 0)
                return;

            await readySignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private PlaybackDirection InferTransportDirection(TimeSpan targetTime)
    {
        if (targetTime > Time)
            return PlaybackDirection.Forward;

        if (targetTime < Time)
            return PlaybackDirection.Backward;

        if (IsCompleted)
            return PlaybackDirection.Backward;

        if (IsBeforeFirstEdge())
            return PlaybackDirection.Forward;

        return currentDirection;
    }

    private async ValueTask<AwaitPoint> RunUntilNextAwaitPointAsync(
        PlaybackDirection direction,
        PlaybackStepGranularity granularity,
        CancellationToken cancellationToken
    )
    {
        var observedSequence = checkpointSequence;

        while (true)
        {
            while (TryRunOneReady())
            {
                if (checkpointSequence == observedSequence)
                    continue;

                var position = GetCurrentBoundaryPosition();
                observedSequence = checkpointSequence;

                if (!IsStepBoundaryIncluded(position, granularity))
                    continue;

                if (position != null)
                    boundaryCursor = new(direction, position.Value);

                return new(true, position);
            }

            if (Volatile.Read(ref pendingExternalEffects) == 0)
                return default;

            await readySignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryRunOneReady()
    {
        Action? action;
        lock (readyGate)
        {
            if (ready.Count == 0)
                return false;

            action = ready.Dequeue();
        }

        action();
        return true;
    }

    private bool HasReady()
    {
        lock (readyGate)
            return ready.Count != 0;
    }

    private async ValueTask<InitialEdgeResult> TryEvaluateInitialForwardEdgeAsync(
        PlaybackDirection direction,
        PlaybackStepGranularity granularity,
        CancellationToken cancellationToken
    )
    {
        if (!IsBeforeFirstEdge())
            return default;

        RestoreToInitial(postReady: true);
        var awaitPoint = await RunUntilNextAwaitPointAsync(
                direction,
                granularity,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new(true, awaitPoint);
    }

    private bool IsBeforeFirstEdge()
    {
        return edgeCursor == PlaybackEdgeCursor.BeforeFirst
            && Time == TimeSpan.Zero
            && rootRunner != null
            && !HasReady();
    }

    private async ValueTask<bool> TryEvaluateTerminalBackwardEdgeAsync()
    {
        if (edgeCursor != PlaybackEdgeCursor.AfterLast)
            return false;

        var checkpoint = FindTerminalEdgeCheckpointRecord();
        if (checkpoint?.EntryCheckpoint == null)
            return false;

        edgeCursor = PlaybackEdgeCursor.None;

        var previousSuppress = suppressImplicitCallContinuationBoundary;
        suppressImplicitCallContinuationBoundary = true;
        PushSuppressCheckpointAutoContinuation();

        try
        {
            RestoreRunnerTreeTo(checkpoint.EntryCheckpoint, false);
            RestoreResumeStoreSnapshot(checkpoint.EntryCheckpoint);
            await RunUntilIdleAsync(currentCancellationToken).ConfigureAwait(false);
            IsCompleted = false;
            currentRecord = checkpoint;
            return true;
        }
        finally
        {
            PopSuppressCheckpointAutoContinuation();
            suppressImplicitCallContinuationBoundary = previousSuppress;
        }
    }

    private CheckpointTimelineRecord? FindTerminalEdgeCheckpointRecord()
    {
        for (var i = records.Count - 1; i >= 0; i--)
            if (records[i] is CheckpointTimelineRecord { EntryCheckpoint: not null } checkpoint)
                return checkpoint;

        return null;
    }

    internal void Post(Action action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        var generation = this.generation;
        lock (readyGate)
        {
            ready.Enqueue(() =>
            {
                if (generation == this.generation)
                    action();
            });
        }

        readySignal.Release();
    }

    internal void AttachRootRunner(IPlaybackRunner runner)
    {
        if (rootRunner != null && !ReferenceEquals(rootRunner, runner))
            throw new NotSupportedException(
                "This playback supports exactly one root PlaybackTask."
            );

        rootRunner = runner;
    }

    internal void RegisterRunnerEntry(
        IPlaybackRunner runner,
        TimelineRecord? parentRecord,
        string debugLabel
    )
    {
        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "entry" : debugLabel;

        CheckpointTimelineRecord record;

        if (Mode == PlaybackMode.Playback)
        {
            var existing = TryConsumeExistingCheckpointRecord(
                debugLabel,
                CheckpointRecordKind.Entry
            );
            if (existing != null)
            {
                existing.OwnerRunner = runner;
                existing.Parent = parentRecord;
                record = existing;
            }
            else
            {
                SwitchToRecordingFromPlaybackCursor();
                record = new(++nextRecordId, Time, debugLabel, CheckpointRecordKind.Entry);

                AddRecord(record, parentRecord, runner);
            }
        }
        else
        {
            record = new(++nextRecordId, Time, debugLabel, CheckpointRecordKind.Entry);

            AddRecord(record, parentRecord, runner);
        }

        var checkpoint = new TimelineCheckpoint(
            ++checkpointSequence,
            runner,
            0,
            Time,
            PlaybackPromiseKind.Checkpoint,
            null,
            parentRecord,
            playbackRecordIndex,
            CaptureStoreSnapshot(),
            Timestamp,
            DeltaTime
        );

        SetEntryCheckpoint(record, checkpoint);
        BindStoreCheckpoint(checkpoint);
    }

    internal CheckpointTimelineRecord GetOrCreateCheckpointRecord(string debugLabel)
    {
        EnsureCurrentPlayback();

        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "Checkpoint" : debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            var existing = TryConsumeExistingCheckpointRecord(
                debugLabel,
                CheckpointRecordKind.User
            );
            if (existing != null)
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = new CheckpointTimelineRecord(
            ++nextRecordId,
            Time,
            debugLabel,
            CheckpointRecordKind.User
        );

        AddRecord(record);
        return record;
    }

    private CheckpointTimelineRecord GetOrCreateImplicitCheckpointRecord(string debugLabel)
    {
        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "ImplicitCheckpoint" : debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            var existing = TryConsumeExistingCheckpointRecord(
                debugLabel,
                CheckpointRecordKind.Implicit
            );
            if (existing != null)
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = new CheckpointTimelineRecord(
            ++nextRecordId,
            Time,
            debugLabel,
            CheckpointRecordKind.Implicit
        );

        AddRecord(record);
        return record;
    }

    private void CreateOrUpdateImplicitCallContinuationCheckpoint(CallTimelineRecord call)
    {
        var boundary = GetOrCreateImplicitCheckpointRecord($"after {call.DebugLabel}");
        var parent = call.ParentRunner;
        var checkpointId = call.ParentAwaitCheckpointId;
        var resumeScope = parent.GetResumeScope(checkpointId);

        var checkpoint = new TimelineCheckpoint(
            ++checkpointSequence,
            parent,
            checkpointId,
            Time,
            PlaybackPromiseKind.Checkpoint,
            null,
            resumeScope,
            playbackRecordIndex,
            CaptureStoreSnapshot(),
            Timestamp,
            DeltaTime
        );

        // Always update: during playback the runner/checkpoint id can be rebound.
        SetEntryCheckpoint(boundary, checkpoint);
        BindStoreCheckpoint(checkpoint);
    }

    internal CallTimelineRecord GetOrCreateCallRecord(
        IPlaybackRunner parentRunner,
        IPlaybackRunner childRunner,
        string debugLabel
    )
    {
        var label = debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            var existing = TryConsumeExistingCallRecord(label, parentRunner, childRunner);

            if (existing != null)
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var call = new CallTimelineRecord(++nextRecordId, Time, label, parentRunner, childRunner);

        AddRecord(call);
        return call;
    }

    internal void OnCheckpointCaptured(
        IPlaybackRunner runner,
        int checkpointId,
        PlaybackPromiseKind awaitKind,
        PlaybackPromiseBase? awaitedPromise,
        TimelineRecord? ownerRecord,
        TimelineRecord? resumeScope
    )
    {
        if (suppressAwaitPointTimestampSamplingDepth == 0)
            SampleTimestamp();

        var checkpoint = new TimelineCheckpoint(
            ++checkpointSequence,
            runner,
            checkpointId,
            Time,
            awaitKind,
            awaitedPromise,
            resumeScope,
            playbackRecordIndex,
            CaptureStoreSnapshot(),
            Timestamp,
            DeltaTime
        );

        if (ownerRecord != null)
        {
            SetEntryCheckpoint(ownerRecord, checkpoint);
            BindStoreCheckpoint(checkpoint);

            if (
                SuppressCheckpointAutoContinuation
                && ownerRecord is CheckpointTimelineRecord checkpointRecord
            )
                pendingForwardCheckpoint = checkpointRecord;
        }
        else
        {
            BindStoreCheckpoint(checkpoint);
        }
    }

    internal void OnRunnerCompleted(IPlaybackRunner runner)
    {
        if (runner.CurrentCallRecord != null)
        {
            var call = runner.CurrentCallRecord;
            call.Complete(Time);

            if (!suppressImplicitCallContinuationBoundary && call.ParentAwaitCheckpointId != 0)
                CreateOrUpdateImplicitCallContinuationCheckpoint(call);

            currentRecord = call;
        }

        if (ReferenceEquals(runner, rootRunner))
        {
            if (currentDirection == PlaybackDirection.Backward)
            {
                IsCompleted = false;
                edgeCursor = PlaybackEdgeCursor.None;
            }
            else
            {
                IsCompleted = true;
                edgeCursor = PlaybackEdgeCursor.AfterLast;
            }
        }
    }

    private static void SetEntryCheckpoint(TimelineRecord record, TimelineCheckpoint checkpoint)
    {
        checkpoint.ResumeStoreSnapshot = record.EntryCheckpoint?.ResumeStoreSnapshot;
        record.EntryCheckpoint = checkpoint;
    }

    internal void OnRunnerFaulted(IPlaybackRunner runner, Exception exception)
    {
        Console.WriteLine($"EXCEPTION: {exception.GetType().Name}: {exception.Message}");
        OnRunnerCompleted(runner);
    }

    internal void DebugLog(string message)
    {
        if (DebugLogging)
            Console.WriteLine("[rewind] " + message);
    }

    private void PushSuppressCheckpointAutoContinuation()
    {
        suppressCheckpointAutoContinuationDepth++;
    }

    private void PopSuppressCheckpointAutoContinuation()
    {
        suppressCheckpointAutoContinuationDepth--;

        if (suppressCheckpointAutoContinuationDepth < 0)
            throw new InvalidOperationException(
                "Checkpoint auto-continuation suppression underflow."
            );
    }

    private async ValueTask TransportToAsync(
        TimeSpan targetTime,
        TransportOptions options,
        PlaybackDirection? directionOverride,
        CancellationToken cancellationToken
    )
    {
        EnsureStarted();
        using var cancellationScope = PushCancellationToken(cancellationToken);

        boundaryCursor = null;

        if (targetTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(targetTime),
                "Target time must be non-negative."
            );

        if (directionOverride == null && targetTime == Time)
        {
            TargetTime = Time;
            return;
        }

        var direction = directionOverride ?? InferTransportDirection(targetTime);

        if (directionOverride != null)
        {
            if (targetTime < Time && direction == PlaybackDirection.Forward)
                throw new ArgumentException(
                    "Forward transport cannot target an earlier time. Omit the direction or use Backward.",
                    "direction"
                );

            if (targetTime > Time && direction == PlaybackDirection.Backward)
                throw new ArgumentException(
                    "Backward transport cannot target a later time. Omit the direction or use Forward.",
                    "direction"
                );
        }

        currentDirection = direction;

        if (
            direction == PlaybackDirection.Forward
            && edgeCursor == PlaybackEdgeCursor.AfterLast
            && targetTime >= Time
        )
        {
            TargetTime = Time;
            return;
        }

        var edgeEvaluated = false;
        if (direction == PlaybackDirection.Forward)
        {
            var initialEdge = await TryEvaluateInitialForwardEdgeAsync(
                    direction,
                    PlaybackStepGranularity.AwaitPoint,
                    cancellationToken
                )
                .ConfigureAwait(false);

            edgeEvaluated = initialEdge.Moved;

            await RunReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RunReadyAsync(cancellationToken).ConfigureAwait(false);
            edgeEvaluated = await TryEvaluateTerminalBackwardEdgeAsync().ConfigureAwait(false);
        }

        TargetTime = targetTime;

        if (edgeEvaluated && targetTime == Time)
            return;

        if (options.Evaluation == TransportEvaluation.Traverse)
        {
            await TraverseToAsync(targetTime, direction, options.EvaluateTarget, cancellationToken)
                .ConfigureAwait(false);

            if (direction == PlaybackDirection.Backward && Time == TimeSpan.Zero)
                RestoreToInitial(postReady: false);

            return;
        }

        await EvaluateTargetOnlyAsync(targetTime, direction, options.EvaluateTarget)
            .ConfigureAwait(false);

        if (direction == PlaybackDirection.Backward && Time == TimeSpan.Zero)
            RestoreToInitial(postReady: false);
    }

    private TimelineBoundary? FindStepBoundary(
        PlaybackDirection direction,
        PlaybackStepGranularity granularity
    )
    {
        TimelineBoundary? best = null;

        foreach (var boundary in EnumerateTimelineBoundaries(GetStepBoundaryScope(direction)))
            Consider(boundary);

        return best;

        void Consider(TimelineBoundary boundary)
        {
            if (!IsStepBoundaryIncluded(boundary, granularity))
                return;

            if (direction == PlaybackDirection.Forward)
            {
                if (boundary.Time < Time)
                    return;

                if (
                    boundary.Time == Time
                    && boundaryCursor
                        is { Direction: PlaybackDirection.Forward, Boundary: var last }
                    && last.Time == Time
                    && boundary.Order <= last.Order
                )
                    return;

                if (best == null || boundary.CompareTo(best.Value) < 0)
                    best = boundary;

                return;
            }

            if (boundary.Time > Time)
                return;

            if (
                boundary.Time == Time
                && boundaryCursor
                    is { Direction: PlaybackDirection.Backward, Boundary: var lastBack }
                && lastBack.Time == Time
                && boundary.Order >= lastBack.Order
            )
                return;

            if (best == null || boundary.CompareTo(best.Value) > 0)
                best = boundary;
        }
    }

    private static TimelineBoundaryScope GetStepBoundaryScope(PlaybackDirection direction)
    {
        return direction == PlaybackDirection.Forward
            ? TimelineBoundaryScope.StepForward
            : TimelineBoundaryScope.StepBackward;
    }

    private static bool IsStepBoundaryIncluded(
        TimelineBoundary? boundary,
        PlaybackStepGranularity granularity
    )
    {
        return boundary == null
            ? granularity == PlaybackStepGranularity.AwaitPoint
            : IsStepBoundaryIncluded(boundary.Value, granularity);
    }

    private static bool IsStepBoundaryIncluded(
        TimelineBoundary boundary,
        PlaybackStepGranularity granularity
    )
    {
        if (granularity == PlaybackStepGranularity.AwaitPoint)
            return true;

        return boundary.Record switch
        {
            CheckpointTimelineRecord checkpoint => checkpoint.CheckpointKind
                == CheckpointRecordKind.User,
            DelayRecord or EffectRecord or SeekLoopRecord => true,
            _ => false,
        };
    }

    private IEnumerable<TimelineBoundary> EnumerateTimelineBoundaries(TimelineBoundaryScope scope)
    {
        var timedBoundaryTimes =
            scope == TimelineBoundaryScope.StepBackward ? GetTimedBoundaryTimes() : null;

        foreach (var record in records)
        {
            switch (record)
            {
                case CheckpointTimelineRecord checkpoint:
                    if (scope == TimelineBoundaryScope.Traversal)
                    {
                        yield return TimelineBoundary.Create(
                            checkpoint,
                            TimelineBoundaryKind.Point
                        );
                    }
                    else if (
                        scope == TimelineBoundaryScope.StepBackward
                        && IsCheckpointStepBoundary(checkpoint, timedBoundaryTimes)
                    )
                    {
                        yield return TimelineBoundary.Create(
                            checkpoint,
                            TimelineBoundaryKind.Point
                        );
                    }

                    break;

                case DelayRecord:
                case EffectRecord:
                case SeekLoopRecord:
                    yield return TimelineBoundary.Create(record, TimelineBoundaryKind.Start);

                    if (record.Duration > TimeSpan.Zero)
                        yield return TimelineBoundary.Create(record, TimelineBoundaryKind.End);

                    break;

                default:
                    if (scope == TimelineBoundaryScope.Traversal)
                    {
                        yield return TimelineBoundary.Create(record, TimelineBoundaryKind.Start);

                        if (record.Duration > TimeSpan.Zero)
                            yield return TimelineBoundary.Create(record, TimelineBoundaryKind.End);
                    }

                    break;
            }
        }
    }

    private TimelineBoundary? GetCurrentStepBoundary()
    {
        var record = currentRecord;
        if (record == null)
            return null;

        switch (record)
        {
            case DelayRecord:
            case EffectRecord:
            case SeekLoopRecord:
                if (Time == record.StartTime)
                    return TimelineBoundary.Create(record, TimelineBoundaryKind.Start);

                if (record.Duration > TimeSpan.Zero && Time == record.EndTime)
                    return TimelineBoundary.Create(record, TimelineBoundaryKind.End);

                break;

            case CheckpointTimelineRecord checkpoint:
                if (IsCheckpointStepBoundary(checkpoint))
                    return TimelineBoundary.Create(checkpoint, TimelineBoundaryKind.Point);

                break;
        }

        return null;
    }

    private TimelineBoundary? GetCurrentBoundaryPosition()
    {
        var record = currentRecord;
        if (record == null)
            return null;

        switch (record)
        {
            case DelayRecord:
            case EffectRecord:
            case SeekLoopRecord:
                if (Time == record.StartTime)
                    return TimelineBoundary.Create(record, TimelineBoundaryKind.Start);

                if (record.Duration > TimeSpan.Zero && Time == record.EndTime)
                    return TimelineBoundary.Create(record, TimelineBoundaryKind.End);

                break;

            case CheckpointTimelineRecord:
                return TimelineBoundary.Create(record, TimelineBoundaryKind.Point);
        }

        return null;
    }

    private bool IsCheckpointStepBoundary(CheckpointTimelineRecord checkpoint)
    {
        return IsCheckpointStepBoundary(checkpoint, null);
    }

    private bool IsCheckpointStepBoundary(
        CheckpointTimelineRecord checkpoint,
        HashSet<TimeSpan>? timedBoundaryTimes
    )
    {
        var overlapsTimedBoundary =
            timedBoundaryTimes?.Contains(checkpoint.StartTime)
            ?? HasTimedBoundaryAt(checkpoint.StartTime);

        if (overlapsTimedBoundary)
            return false;

        return checkpoint.CheckpointKind switch
        {
            CheckpointRecordKind.Entry => true,
            CheckpointRecordKind.User => HasLaterRecordForRunner(checkpoint),
            CheckpointRecordKind.Implicit => false,
            _ => false,
        };
    }

    private HashSet<TimeSpan> GetTimedBoundaryTimes()
    {
        var result = new HashSet<TimeSpan>();

        foreach (var record in records)
        {
            switch (record)
            {
                case DelayRecord:
                case SeekLoopRecord:
                    result.Add(record.StartTime);

                    if (record.Duration > TimeSpan.Zero)
                        result.Add(record.EndTime);

                    break;
            }
        }

        return result;
    }

    private bool HasLaterRecordForRunner(TimelineRecord record)
    {
        for (var i = record.FlatIndex + 1; i < records.Count; i++)
            if (ReferenceEquals(records[i].OwnerRunner, record.OwnerRunner))
                return true;

        return false;
    }

    private bool HasTimedBoundaryAt(TimeSpan time)
    {
        foreach (var record in records)
        {
            switch (record)
            {
                case DelayRecord:
                case SeekLoopRecord:
                    if (record.StartTime == time)
                        return true;

                    if (record.Duration > TimeSpan.Zero && record.EndTime == time)
                        return true;

                    break;
            }
        }

        return false;
    }

    private async ValueTask EvaluateStepBoundaryAsync(
        TimelineBoundary boundary,
        PlaybackDirection direction
    )
    {
        MoveTimeTo(boundary.Time);

        if (boundary.Kind == TimelineBoundaryKind.Start && boundary.Record is DelayRecord)
        {
            if (direction == PlaybackDirection.Backward)
                RestoreToRecord(boundary.Record);

            MoveTimeTo(boundary.Time);
            currentRecord = boundary.Record;
            return;
        }

        await EvaluateRecordAsync(boundary.Record, boundary.Time, direction).ConfigureAwait(false);
        MoveTimeTo(boundary.Time);
    }

    private async ValueTask EvaluateTargetOnlyAsync(
        TimeSpan targetTime,
        PlaybackDirection direction,
        bool evaluateTarget
    )
    {
        if (
            evaluateTarget
            && await EvaluateAtAsync(targetTime, direction, true).ConfigureAwait(false)
        )
            return;

        MoveToTimelineGap(targetTime);
    }

    private async ValueTask TraverseToAsync(
        TimeSpan targetTime,
        PlaybackDirection direction,
        bool evaluateTarget,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var boundary = FindNextBoundary(Time, targetTime, direction);
            if (boundary == null)
                break;

            await EvaluateAtAsync(boundary.Value.Time, direction, true).ConfigureAwait(false);
            await RunReadyAsync(cancellationToken).ConfigureAwait(false);

            if (
                direction == PlaybackDirection.Forward
                && edgeCursor == PlaybackEdgeCursor.AfterLast
            )
                return;

            if (Time == targetTime)
                return;
        }

        if (evaluateTarget)
        {
            if (await EvaluateAtAsync(targetTime, direction, true).ConfigureAwait(false))
            {
                if (
                    direction == PlaybackDirection.Forward
                    && edgeCursor == PlaybackEdgeCursor.AfterLast
                )
                    return;

                return;
            }
        }

        MoveToTimelineGap(targetTime);
    }

    private TimelineBoundary? FindNextBoundary(
        TimeSpan from,
        TimeSpan to,
        PlaybackDirection direction
    )
    {
        TimelineBoundary? best = null;

        foreach (var boundary in EnumerateTimelineBoundaries(TimelineBoundaryScope.Traversal))
            Consider(boundary);

        return best;

        void Consider(TimelineBoundary boundary)
        {
            if (!IsBetween(boundary.Time, from, to, direction))
                return;

            if (best == null)
            {
                best = boundary;
                return;
            }

            if (direction == PlaybackDirection.Forward)
            {
                if (boundary.CompareTo(best.Value) < 0)
                    best = boundary;
            }
            else
            {
                if (boundary.CompareTo(best.Value) > 0)
                    best = boundary;
            }
        }
    }

    private static bool IsBetween(
        TimeSpan time,
        TimeSpan from,
        TimeSpan to,
        PlaybackDirection direction
    )
    {
        return direction == PlaybackDirection.Forward
            ? from < time && time <= to
            : to <= time && time < from;
    }

    private async ValueTask<bool> EvaluateAtAsync(
        TimeSpan time,
        PlaybackDirection direction,
        bool includeLoopEnd
    )
    {
        var evaluatedIds = new HashSet<int>();
        var evaluatedAny = false;

        MoveTimeTo(time);

        while (true)
        {
            var records = GetEvaluatableRecordsAt(time, direction, includeLoopEnd, evaluatedIds);

            if (records.Count == 0)
                break;

            var record = records[0];
            evaluatedIds.Add(record.Id);

            await EvaluateRecordAsync(record, time, direction).ConfigureAwait(false);
            evaluatedAny = true;

            await RunReadyAsync(currentCancellationToken).ConfigureAwait(false);
            MoveTimeTo(time);
        }

        return evaluatedAny;
    }

    private List<TimelineRecord> GetEvaluatableRecordsAt(
        TimeSpan time,
        PlaybackDirection direction,
        bool includeLoopEnd,
        HashSet<int> evaluatedIds
    )
    {
        var result = new List<TimelineRecord>();

        foreach (var record in records)
        {
            if (evaluatedIds.Contains(record.Id))
                continue;

            switch (record)
            {
                case DelayRecord delay:
                    if (delay.EndTime == time)
                        result.Add(delay);
                    break;

                case EffectRecord effect:
                    if (effect.StartTime == time)
                        result.Add(effect);
                    break;

                case SeekLoopRecord loop:
                    if (loop.Contains(time, includeLoopEnd))
                        result.Add(loop);
                    break;

                case CheckpointTimelineRecord checkpoint:
                    // Checkpoints are source-segment boundaries. Forward execution
                    // reaches them naturally via RunReady(); direct evaluation is only
                    // needed for backward traversal / target sampling.
                    if (
                        direction == PlaybackDirection.Backward
                        && checkpoint.CheckpointKind != CheckpointRecordKind.Implicit
                        && checkpoint.StartTime == time
                    )
                        result.Add(checkpoint);
                    break;
            }
        }

        if (direction == PlaybackDirection.Forward)
            result.Sort(static (a, b) => a.FlatIndex.CompareTo(b.FlatIndex));
        else
            result.Sort(static (a, b) => b.FlatIndex.CompareTo(a.FlatIndex));

        return result;
    }

    private ValueTask EvaluateRecordAsync(
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        return record switch
        {
            DelayRecord delay => EmitDelayAtAsync(delay, time, direction),
            EffectRecord effect => EmitEffectAtAsync(effect, direction),
            SeekLoopRecord loop => EmitSeekLoopAtAsync(loop, time, direction),
            CheckpointTimelineRecord checkpoint => EmitCheckpointAsync(checkpoint, direction),
            _ => ValueTask.CompletedTask,
        };
    }

    private async ValueTask EmitDelayAtAsync(
        DelayRecord delay,
        TimeSpan targetTime,
        PlaybackDirection direction
    )
    {
        var mustRestore = direction == PlaybackDirection.Backward || !delay.HasPendingDelay;

        if (mustRestore)
            RestoreToRecord(delay);

        MoveTimeTo(targetTime);

        delay.Complete();
        await RunReadyAsync(currentCancellationToken).ConfigureAwait(false);

        currentRecord = delay;
    }

    private async ValueTask EmitEffectAtAsync(EffectRecord effect, PlaybackDirection direction)
    {
        if (direction == PlaybackDirection.Backward)
        {
            MoveTimeTo(effect.StartTime);
            RestoreStoreSnapshot(effect.EntryCheckpoint?.StoreSnapshot);
            currentRecord = effect;
            Mode = PlaybackMode.Playback;
            playbackRecordIndex = Math.Min(effect.FlatIndex + 1, records.Count);
            return;
        }

        RestoreToRecord(effect);
        await RunReadyAsync(currentCancellationToken).ConfigureAwait(false);
        currentRecord = effect;
    }

    private async ValueTask EmitSeekLoopAtAsync(
        SeekLoopRecord loop,
        TimeSpan targetTime,
        PlaybackDirection direction
    )
    {
        var active = FindActiveSeekLoop(loop);

        var mustRestore = direction == PlaybackDirection.Backward || active == null;

        if (mustRestore)
            RestoreToRecord(loop);

        MoveTimeTo(targetTime);

        if (direction == PlaybackDirection.Backward && targetTime == loop.EndTime)
            suppressLoopExitFor = loop;

        loop.EmitTrueAt(targetTime);
        await RunReadyAsync(currentCancellationToken).ConfigureAwait(false);

        currentRecord = loop;
    }

    private async ValueTask EmitCheckpointAsync(
        CheckpointTimelineRecord checkpoint,
        PlaybackDirection direction
    )
    {
        if (checkpoint.EntryCheckpoint == null)
            return;

        // A checkpoint is an await-segment boundary. When evaluating it as part
        // of backward traversal, run only that segment. Do not let a child
        // method's completion synthesize/re-enter the parent continuation again;
        // parent-side continuation boundaries are represented by their own
        // implicit checkpoint records.
        var reconnectParents = direction == PlaybackDirection.Forward;

        var previousSuppress = suppressImplicitCallContinuationBoundary;
        if (direction == PlaybackDirection.Backward)
            suppressImplicitCallContinuationBoundary = true;

        if (direction == PlaybackDirection.Backward)
            PushSuppressCheckpointAutoContinuation();

        try
        {
            RestoreRunnerTreeTo(checkpoint.EntryCheckpoint, reconnectParents);
            RestoreResumeStoreSnapshot(checkpoint.EntryCheckpoint);
            currentRecord = checkpoint;

            await RunUntilIdleAsync(currentCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (direction == PlaybackDirection.Backward)
                PopSuppressCheckpointAutoContinuation();

            suppressImplicitCallContinuationBoundary = previousSuppress;
        }

        currentRecord = checkpoint;
    }

    private SeekLoopRecord? FindActiveSeekLoop(SeekLoopRecord loop)
    {
        foreach (var active in activeSeekLoops)
            if (ReferenceEquals(active, loop) && active.HasPendingMoveNext)
                return active;

        return null;
    }

    private EffectRecord GetOrCreateEffectRecord(
        string debugLabel,
        Func<CancellationToken, ValueTask<object?>> executeAsync
    )
    {
        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "Effect" : debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            var existing = TryConsumeExistingEffectRecord(debugLabel);
            if (existing != null)
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = new EffectRecord(++nextRecordId, Time, debugLabel, executeAsync);

        AddRecord(record);
        return record;
    }

    private DelayRecord GetOrCreateDelayRecord(TimeSpan duration, string debugLabel)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration must be non-negative."
            );

        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "Delay" : debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            var existing = TryConsumeExistingDelayRecord(duration, debugLabel);
            if (existing != null)
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = new DelayRecord(++nextRecordId, Time, duration, debugLabel);

        AddRecord(record);
        return record;
    }

    private SeekLoopRecord GetOrCreateSeekLoopRecord(TimeSpan duration, string debugLabel)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration must be non-negative."
            );

        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "ForEachOnSeek" : debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            var existing = TryConsumeExistingSeekLoopRecord(duration, debugLabel);
            if (existing != null)
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = new SeekLoopRecord(++nextRecordId, Time, duration, debugLabel);

        AddRecord(record);
        return record;
    }

    private void AddRecord(
        TimelineRecord record,
        TimelineRecord? parentOverride = null,
        IPlaybackRunner? ownerRunnerOverride = null
    )
    {
        record.FlatIndex = records.Count;
        record.OwnerRunner = ownerRunnerOverride ?? PlaybackRuntime.CurrentRunner;

        var parent =
            parentOverride
            ?? PlaybackRuntime.CurrentRecordScope
            ?? PlaybackRuntime.CurrentRunner?.CurrentCallRecord;

        record.Parent = parent;

        if (parent != null)
            parent.Children.Add(record);

        records.Add(record);

        currentRecord = record;
        playbackRecordIndex = records.Count;
    }

    private CheckpointTimelineRecord? TryConsumeExistingCheckpointRecord(
        string debugLabel,
        CheckpointRecordKind checkpointKind
    )
    {
        if (playbackRecordIndex >= records.Count)
            return null;

        if (records[playbackRecordIndex] is not CheckpointTimelineRecord checkpoint)
            return null;

        if (
            checkpoint.StartTime != Time
            || checkpoint.DebugLabel != debugLabel
            || checkpoint.CheckpointKind != checkpointKind
        )
            return null;

        playbackRecordIndex++;
        currentRecord = checkpoint;
        return checkpoint;
    }

    private DelayRecord? TryConsumeExistingDelayRecord(TimeSpan duration, string debugLabel)
    {
        if (playbackRecordIndex >= records.Count)
            return null;

        if (records[playbackRecordIndex] is not DelayRecord delay)
            return null;

        if (delay.StartTime != Time || delay.Duration != duration || delay.DebugLabel != debugLabel)
            return null;

        playbackRecordIndex++;
        currentRecord = delay;
        return delay;
    }

    private EffectRecord? TryConsumeExistingEffectRecord(string debugLabel)
    {
        if (playbackRecordIndex >= records.Count)
            return null;

        if (records[playbackRecordIndex] is not EffectRecord effect)
            return null;

        if (effect.StartTime != Time || effect.DebugLabel != debugLabel)
            return null;

        playbackRecordIndex++;
        currentRecord = effect;
        return effect;
    }

    private SeekLoopRecord? TryConsumeExistingSeekLoopRecord(TimeSpan duration, string debugLabel)
    {
        if (playbackRecordIndex >= records.Count)
            return null;

        if (records[playbackRecordIndex] is not SeekLoopRecord loop)
            return null;

        if (loop.StartTime != Time || loop.Duration != duration || loop.DebugLabel != debugLabel)
            return null;

        playbackRecordIndex++;
        currentRecord = loop;
        return loop;
    }

    private CallTimelineRecord? TryConsumeExistingCallRecord(
        string debugLabel,
        IPlaybackRunner parentRunner,
        IPlaybackRunner childRunner
    )
    {
        if (playbackRecordIndex >= records.Count)
            return null;

        if (records[playbackRecordIndex] is not CallTimelineRecord call)
            return null;

        if (call.StartTime != Time || call.DebugLabel != debugLabel)
            return null;

        call.RebindRunners(parentRunner, childRunner);

        playbackRecordIndex++;
        currentRecord = call;
        return call;
    }

    private void SwitchToRecordingFromPlaybackCursor()
    {
        if (Mode == PlaybackMode.Recording)
            return;

        TruncateRecordsFrom(playbackRecordIndex);
        Mode = PlaybackMode.Recording;
    }

    private void TruncateRecordsFrom(int index)
    {
        index = Math.Clamp(index, 0, records.Count);

        for (var i = records.Count - 1; i >= index; i--)
            records.RemoveAt(i);

        nextRecordId = records.Count == 0 ? 0 : records.Max(static record => record.Id);

        currentRecord = records.Count == 0 ? null : records[^1];

        playbackRecordIndex = records.Count;

        RebuildRecordIndexes();
    }

    private void RebuildRecordIndexes()
    {
        var live = new HashSet<TimelineRecord>(records);

        foreach (var record in records)
            record.Children.Clear();

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            record.FlatIndex = i;

            if (record.Parent != null && live.Contains(record.Parent))
            {
                record.Parent.Children.Add(record);
            }
            else
            {
                record.Parent = null;
            }
        }
    }

    private void MoveToTimelineGap(TimeSpan targetTime)
    {
        ready.Clear();
        activeSeekLoops.Clear();
        suppressLoopExitFor = null;
        pendingForwardCheckpoint = null;

        MoveTimeTo(targetTime);
        edgeCursor = PlaybackEdgeCursor.None;

        var nearest = FindNearestRecordAtOrBefore(targetTime);
        currentRecord = nearest;
        RestoreStoreSnapshot(nearest?.EntryCheckpoint?.StoreSnapshot);

        Mode = records.Count == 0 ? PlaybackMode.Recording : PlaybackMode.Playback;

        playbackRecordIndex = nearest == null ? 0 : Math.Min(nearest.FlatIndex + 1, records.Count);
    }

    private TimelineRecord? FindNearestRecordAtOrBefore(TimeSpan targetTime)
    {
        TimelineRecord? best = null;

        foreach (var record in records)
        {
            if (record.StartTime > targetTime)
                continue;

            if (best == null || record.FlatIndex > best.FlatIndex)
                best = record;
        }

        return best;
    }

    private void RestoreToRecord(TimelineRecord record)
    {
        if (record.EntryCheckpoint == null)
        {
            MoveToTimelineGap(record.StartTime);
            return;
        }

        RestoreRunnerTreeTo(record.EntryCheckpoint, true);

        currentRecord = record;
    }

    private void RestoreRunnerTreeTo(TimelineCheckpoint target, bool reconnectParentContinuations)
    {
        generation++;
        ready.Clear();
        activeSeekLoops.Clear();
        suppressLoopExitFor = null;
        pendingForwardCheckpoint = null;

        foreach (var record in records)
            record.ResetPlaybackState();

        RebuildRecordIndexes();

        playbackRecordIndex = target.RecordCountAtCapture;
        Mode = PlaybackMode.Playback;
        IsCompleted = false;
        edgeCursor = PlaybackEdgeCursor.None;
        RestoreStoreSnapshot(target.StoreSnapshot);

        var chain = BuildRunnerChain(target.Runner);

        for (var i = 0; i < chain.Count - 1; i++)
        {
            var parent = chain[i];
            var child = chain[i + 1];

            if (child.ParentAwaitCheckpointId == 0)
                throw new InvalidOperationException("Child runner has no parent await checkpoint.");

            parent.RestoreCheckpoint(child.ParentAwaitCheckpointId);
        }

        target.Runner.RestoreCheckpoint(target.CheckpointId);

        if (reconnectParentContinuations)
            for (var i = 1; i < chain.Count; i++)
            {
                var child = chain[i];
                var parent = chain[i - 1];
                var scope = parent.GetResumeScope(child.ParentAwaitCheckpointId);

                child.OwnPromise.AddRunnerContinuation(
                    parent,
                    child.ParentAwaitCheckpointId,
                    parent.Epoch,
                    scope
                );
            }

        MoveTimeTo(target.Time);
        ArmCheckpoint(target);
    }

    private StoreSnapshot CaptureStoreSnapshot()
    {
        return new(hasStoredState, storedState);
    }

    private void RestoreResumeStoreSnapshot(TimelineCheckpoint checkpoint)
    {
        RestoreStoreSnapshot(checkpoint.ResumeStoreSnapshot ?? checkpoint.StoreSnapshot);
    }

    private void StartEffect(EffectRecord record, PlaybackPromiseBase promise)
    {
        var cancellationToken = currentCancellationToken;
        var startTimestamp = TimeProvider.GetTimestamp();
        var direction = currentDirection;
        Interlocked.Increment(ref pendingExternalEffects);

        _ = RunEffectAsync(record, promise, startTimestamp, direction, cancellationToken);
    }

    private async Task RunEffectAsync(
        EffectRecord record,
        PlaybackPromiseBase promise,
        long startTimestamp,
        PlaybackDirection direction,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await record.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var endTimestamp = TimeProvider.GetTimestamp();
            Post(() =>
                CompleteEffect(record, promise, startTimestamp, endTimestamp, direction, result)
            );
        }
        catch (Exception exception)
        {
            var endTimestamp = TimeProvider.GetTimestamp();
            Post(() =>
                FailEffect(record, promise, startTimestamp, endTimestamp, direction, exception)
            );
        }
        finally
        {
            Interlocked.Decrement(ref pendingExternalEffects);
            readySignal.Release();
        }
    }

    private void CompleteEffect(
        EffectRecord record,
        PlaybackPromiseBase promise,
        long startTimestamp,
        long endTimestamp,
        PlaybackDirection direction,
        object? result
    )
    {
        CompleteEffectRecord(record, promise, startTimestamp, endTimestamp, direction);
        promise.TrySetObjectResult(result);
    }

    private void FailEffect(
        EffectRecord record,
        PlaybackPromiseBase promise,
        long startTimestamp,
        long endTimestamp,
        PlaybackDirection direction,
        Exception exception
    )
    {
        CompleteEffectRecord(record, promise, startTimestamp, endTimestamp, direction);
        promise.TrySetException(exception);
    }

    private void CompleteEffectRecord(
        EffectRecord record,
        PlaybackPromiseBase promise,
        long startTimestamp,
        long endTimestamp,
        PlaybackDirection direction
    )
    {
        var elapsed = TimeProvider.GetElapsedTime(startTimestamp, endTimestamp);
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        timestamp = endTimestamp;
        DeltaTime = elapsed;
        hasTimestamp = true;

        if (direction == PlaybackDirection.Forward)
        {
            record.Complete(record.StartTime + elapsed);
            promise.Duration = record.Duration;
            promise.DueTime = record.EndTime;
            MoveTimeTo(record.EndTime);
        }
    }

    private void RestoreStoreSnapshot(StoreSnapshot? snapshot)
    {
        if (snapshot is { HasValue: true })
        {
            storedState = snapshot.Value;
            hasStoredState = true;
            return;
        }

        ClearStoredState();
    }

    private void ClearStoredState()
    {
        storedState = null;
        hasStoredState = false;
    }

    private static List<IPlaybackRunner> BuildRunnerChain(IPlaybackRunner runner)
    {
        var chain = new List<IPlaybackRunner>();

        for (var cursor = runner; cursor != null; cursor = cursor.ParentRunner)
            chain.Add(cursor);

        chain.Reverse();
        return chain;
    }

    private void RestoreToInitial(bool postReady)
    {
        if (rootRunner == null)
            throw new InvalidOperationException("Root runner has not been created.");

        generation++;
        ready.Clear();
        activeSeekLoops.Clear();
        suppressLoopExitFor = null;
        pendingForwardCheckpoint = null;

        foreach (var record in records)
            record.ResetPlaybackState();

        RebuildRecordIndexes();

        playbackRecordIndex = GetInitialPlaybackIndex();
        currentRecord = null;
        IsCompleted = false;
        edgeCursor = postReady ? PlaybackEdgeCursor.None : PlaybackEdgeCursor.BeforeFirst;
        Mode = records.Count == 0 ? PlaybackMode.Recording : PlaybackMode.Playback;
        RestoreStoreSnapshot(null);

        rootRunner.RestoreInitialCheckpoint();

        MoveTimeTo(TimeSpan.Zero);

        if (postReady)
            Post(rootRunner.MoveNext);
    }

    private int GetInitialPlaybackIndex()
    {
        if (records.Count == 0)
            return 0;

        if (
            rootRunner != null
            && records[0] is CheckpointTimelineRecord checkpoint
            && ReferenceEquals(checkpoint.OwnerRunner, rootRunner)
            && checkpoint.StartTime == TimeSpan.Zero
        )
            return 1;

        return 0;
    }

    private void ArmCheckpoint(TimelineCheckpoint checkpoint)
    {
        switch (checkpoint.AwaitKind)
        {
            case PlaybackPromiseKind.Checkpoint:
            {
                var expectedEpoch = checkpoint.Runner.Epoch;
                var resumeScope = checkpoint.ResumeScope;

                Post(() =>
                {
                    checkpoint.Runner.ResumeFromAwait(
                        checkpoint.CheckpointId,
                        expectedEpoch,
                        resumeScope
                    );
                });

                break;
            }

            case PlaybackPromiseKind.Yield:
            {
                var promise =
                    checkpoint.AwaitedPromise as PlaybackPromise
                    ?? throw new InvalidOperationException("Yield checkpoint has no promise.");

                promise.ResetForReplay();

                promise.AddRunnerContinuation(
                    checkpoint.Runner,
                    checkpoint.CheckpointId,
                    checkpoint.Runner.Epoch,
                    checkpoint.ResumeScope
                );

                Post(() => promise.TrySetResult());
                break;
            }

            case PlaybackPromiseKind.Delay:
            {
                var promise =
                    checkpoint.AwaitedPromise as PlaybackPromise
                    ?? throw new InvalidOperationException("Delay checkpoint has no promise.");

                promise.ResetForReplay();

                promise.AddRunnerContinuation(
                    checkpoint.Runner,
                    checkpoint.CheckpointId,
                    checkpoint.Runner.Epoch,
                    checkpoint.ResumeScope
                );

                var delay =
                    promise.OwnerRecord as DelayRecord
                    ?? throw new InvalidOperationException("Delay checkpoint has no delay record.");

                delay.ArmDelay(promise);

                if (delay.Duration == TimeSpan.Zero)
                    Post(() => delay.Complete());

                break;
            }

            case PlaybackPromiseKind.Effect:
            {
                var promise =
                    checkpoint.AwaitedPromise
                    ?? throw new InvalidOperationException("Effect checkpoint has no promise.");

                promise.ResetForReplay();

                promise.AddRunnerContinuation(
                    checkpoint.Runner,
                    checkpoint.CheckpointId,
                    checkpoint.Runner.Epoch,
                    checkpoint.ResumeScope
                );

                var effect =
                    promise.OwnerRecord as EffectRecord
                    ?? throw new InvalidOperationException(
                        "Effect checkpoint has no effect record."
                    );

                StartEffect(effect, promise);
                break;
            }

            case PlaybackPromiseKind.SeekLoopMoveNext:
            {
                var promise =
                    checkpoint.AwaitedPromise as PlaybackPromise<bool>
                    ?? throw new InvalidOperationException("SeekLoop checkpoint has no promise.");

                promise.ResetForReplay();

                promise.AddRunnerContinuation(
                    checkpoint.Runner,
                    checkpoint.CheckpointId,
                    checkpoint.Runner.Epoch,
                    checkpoint.ResumeScope
                );

                switch (promise.OwnerRecord)
                {
                    case SeekLoopRecord loop:
                        loop.ArmMoveNext(promise);

                        if (!activeSeekLoops.Contains(loop))
                            activeSeekLoops.Add(loop);

                        break;

                    case CheckpointTimelineRecord:
                        // This is the implicit await-foreach exit checkpoint.
                        // Resume the state machine with MoveNextAsync() == false.
                        Post(() => promise.TrySetResult(false));
                        break;

                    default:
                        throw new InvalidOperationException(
                            "SeekLoopMoveNext promise has no supported owner record."
                        );
                }

                break;
            }

            case PlaybackPromiseKind.AsyncMethod:
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void MoveTimeTo(TimeSpan time)
    {
        Time = time;
    }

    public void ResetTimestamp()
    {
        timestamp = TimeProvider.GetTimestamp();
        DeltaTime = TimeSpan.Zero;
        hasTimestamp = true;
    }

    private void SampleTimestamp()
    {
        var previous = timestamp;
        var current = TimeProvider.GetTimestamp();

        timestamp = current;
        DeltaTime = hasTimestamp ? TimeProvider.GetElapsedTime(previous, current) : TimeSpan.Zero;
        hasTimestamp = true;
    }

    private TimestampSamplingScope SuppressAwaitPointTimestampSampling()
    {
        suppressAwaitPointTimestampSamplingDepth++;
        return new(this);
    }

    private readonly struct TimestampSamplingScope : IDisposable
    {
        private readonly Playback playback;

        public TimestampSamplingScope(Playback playback)
        {
            this.playback = playback;
        }

        public void Dispose()
        {
            playback.suppressAwaitPointTimestampSamplingDepth--;

            if (playback.suppressAwaitPointTimestampSamplingDepth < 0)
                throw new InvalidOperationException("Timestamp sampling suppression underflow.");
        }
    }

    private IReadOnlyList<TimelineRecordInfo> GetRecordInfos()
    {
        var result = new TimelineRecordInfo[records.Count];

        for (var i = 0; i < records.Count; i++)
            result[i] = records[i].ToInfo();

        return result;
    }

    private StepResult CreateStepResult(bool moved, TimelineBoundary? boundary)
    {
        return new(
            moved,
            Time,
            Timestamp,
            DeltaTime,
            boundary?.Record.ToInfo(),
            boundary == null ? null : ToPublicBoundaryKind(boundary.Value.Kind)
        );
    }

    private static PlaybackBoundaryKind ToPublicBoundaryKind(TimelineBoundaryKind kind)
    {
        return kind switch
        {
            TimelineBoundaryKind.Point => PlaybackBoundaryKind.Point,
            TimelineBoundaryKind.Start => PlaybackBoundaryKind.Start,
            TimelineBoundaryKind.End => PlaybackBoundaryKind.End,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static long AbsTicks(TimeSpan value)
    {
        return value.Ticks == long.MinValue ? long.MaxValue : Math.Abs(value.Ticks);
    }

    private readonly record struct BoundaryCursor(
        PlaybackDirection Direction,
        TimelineBoundary Boundary
    );

    private readonly record struct RunnerStateKey(IPlaybackRunner Runner, int CheckpointId);

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
