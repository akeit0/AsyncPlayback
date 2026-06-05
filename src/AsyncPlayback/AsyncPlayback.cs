using System.Runtime.InteropServices;

namespace AsyncPlayback;

public enum TransportEvaluation
{
    TargetOnly,
    Traverse,
}

public enum PlaybackMoveMode
{
    TargetOnly,
    Traverse,
}

internal enum PlaybackTransportSource
{
    Move,
    Clock,
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

    private static readonly Action<PostedEffectCompletion> CompletePostedEffect =
        static completion =>
        {
            completion.Playback.CompleteEffect(
                completion.RecordId,
                completion.Promise,
                completion.StartTimestamp,
                completion.EndTimestamp,
                completion.Direction,
                completion.Result
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
    private readonly List<TimelineRecord> records = [];
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

    public event Action<PlaybackEvent>? EventOccurred;

    public IReadOnlyList<TimelineRecordInfo> Records => GetRecordInfos();
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

        var promise = new PlaybackPromise(this, PlaybackPromiseKind.Yield)
        {
            StartTime = Time,
            Duration = TimeSpan.Zero,
            DueTime = Time,
        };

        Post(promise, CompletePromise);
        return new(promise);
    }

    public PlaybackTask Delay(TimeSpan duration, string debugLabel = "Delay")
    {
        var playback = this;

        var record = playback.UseDelayRecord(duration, debugLabel);
        if (currentDirection == PlaybackDirection.Backward)
            return PlaybackTask.SuspendReplayAt(PlaybackPromiseKind.Delay, record.FlatIndex);

        var promise = new PlaybackPromise(playback, PlaybackPromiseKind.Delay)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            DueTime = record.EndTime,
            OwnerRecordIndex = record.FlatIndex,
        };

        recordRuntime.ArmDelay(record.FlatIndex, promise);

        if (record.Duration == TimeSpan.Zero)
            playback.Post(new PostedDelayCompletion(playback, record.FlatIndex), CompleteDelay);

        return new(promise);
    }

    public PlaybackTask Effect(Func<ValueTask> effect, string debugLabel = "Effect")
    {
        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        return Effect(_ => effect(), debugLabel);
    }

    public PlaybackTask Effect(
        Func<ValueTask> effect,
        Func<ValueTask>? revert,
        string debugLabel = "Effect"
    )
    {
        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        return Effect(_ => effect(), revert == null ? null : _ => revert(), debugLabel);
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

        var record = UseEffectRecord(
            debugLabel,
            async cancellationToken =>
            {
                await effect(cancellationToken).ConfigureAwait(false);
                return null;
            }
        );

        if (currentDirection == PlaybackDirection.Backward)
        {
            if (revert != null)
            {
                var revertFailure = StartReplayRevertEffect(revert);
                if (revertFailure != null)
                    return PlaybackTask.FromException(revertFailure);
            }

            return PlaybackTask.SuspendReplayAt(PlaybackPromiseKind.Effect, record.FlatIndex);
        }

        var promise = new PlaybackPromise(this, PlaybackPromiseKind.Effect)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            DueTime = record.EndTime,
            OwnerRecordIndex = record.FlatIndex,
        };

        StartEffect(record.Id, promise);
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

        var record = UseEffectRecord(
            debugLabel,
            async cancellationToken => await effect(cancellationToken).ConfigureAwait(false)
        );

        if (currentDirection == PlaybackDirection.Backward)
            return PlaybackTask<T>.SuspendReplayAt(PlaybackPromiseKind.Effect, record.FlatIndex);

        if (currentDirection == PlaybackDirection.Backward)
        {
            if (!record.TryGetEffectResult(out var result))
                throw new InvalidOperationException(
                    $"Effect record '{record.DebugLabel}' has no recorded result."
                );

            return PlaybackTask<T>.FromResult((T)result!);
        }

        var promise = new PlaybackPromise<T>(this, PlaybackPromiseKind.Effect)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            DueTime = record.EndTime,
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
        return record.Kind == TimelineRecordKind.SeekLoop
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
            return PlaybackTask<bool>.SuspendReplayAt(
                PlaybackPromiseKind.SeekLoopMoveNext,
                ownerRecord.FlatIndex
            );

        var promise = new PlaybackPromise<bool>(this, PlaybackPromiseKind.SeekLoopMoveNext)
        {
            StartTime = record.StartTime,
            Duration = record.Duration,
            DueTime = record.EndTime,
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
        if ((uint)recordIndex >= (uint)records.Count)
            throw new ArgumentOutOfRangeException(nameof(recordIndex));

        return records[recordIndex];
    }

    private ref TimelineRecord GetRecordRef(int recordIndex)
    {
        if ((uint)recordIndex >= (uint)records.Count)
            throw new ArgumentOutOfRangeException(nameof(recordIndex));

        return ref CollectionsMarshal.AsSpan(records)[recordIndex];
    }

    private TimelineRecord GetRecord(RecordId recordId)
    {
        return GetRecord(GetRecordIndex(recordId));
    }

    internal int GetRecordIndex(RecordId recordId)
    {
        for (var i = 0; i < records.Count; i++)
            if (records[i].Id == recordId)
                return i;

        throw new InvalidOperationException($"Timeline record #{recordId} does not exist.");
    }

    private TimelineRecord? GetCurrentRecord()
    {
        return currentRecordIndex is { } index ? GetRecord(index) : null;
    }

    private void SetCurrentRecord(RecordId recordId)
    {
        currentRecordIndex = GetRecordIndex(recordId);
    }

    private void SetCurrentRecord(int? recordIndex)
    {
        currentRecordIndex = recordIndex;
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

        return TransportToAsync(
            target,
            options,
            direction,
            PlaybackTransportSource.Move,
            cancellationToken
        );
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
        var target = Time + DeltaTime;

        await TransportToAsync(
                target,
                options,
                DeltaTime > TimeSpan.Zero ? PlaybackDirection.Forward
                    : DeltaTime < TimeSpan.Zero ? PlaybackDirection.Backward
                    : null,
                PlaybackTransportSource.Clock,
                cancellationToken
            )
            .ConfigureAwait(false);
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

    public ValueTask MoveToAsync(
        TimeSpan targetTime,
        PlaybackMoveMode mode = PlaybackMoveMode.Traverse,
        PlaybackDirection? direction = null,
        bool evaluateTarget = true,
        CancellationToken cancellationToken = default
    )
    {
        return TransportToAsync(
            targetTime,
            new(ToTransportEvaluation(mode), evaluateTarget),
            direction,
            PlaybackTransportSource.Move,
            cancellationToken
        );
    }

    public ValueTask MoveToAsync(
        TimeSpan targetTime,
        PlaybackDirection direction,
        CancellationToken cancellationToken = default
    )
    {
        return MoveToAsync(
            targetTime,
            PlaybackMoveMode.Traverse,
            direction,
            evaluateTarget: true,
            cancellationToken
        );
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
        ResetBoundaryEventDeduplication();
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
            if (capturedAwaitPoint.Boundary is { } seekStart && IsSeekLoopStartBoundary(seekStart))
                await EvaluateStepBoundaryAsync(seekStart, direction).ConfigureAwait(false);

            return CreateStepResult(true, capturedAwaitPoint.Boundary);
        }

        if (
            direction == PlaybackDirection.Forward
            && !HasReady()
            && pendingForwardCheckpointIndex is { } pendingCheckpointIndex
            && GetRecord(pendingCheckpointIndex)
                is { Kind: TimelineRecordKind.Checkpoint, EntryCheckpoint: not null } checkpoint
        )
        {
            pendingForwardCheckpointIndex = null;
            RestoreRunnerTreeTo(checkpoint.EntryCheckpoint!, true);

            capturedAwaitPoint = await RunUntilNextAwaitPointAsync(
                    direction,
                    granularity,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (capturedAwaitPoint.Stopped)
            {
                if (
                    capturedAwaitPoint.Boundary is { } seekStart
                    && IsSeekLoopStartBoundary(seekStart)
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

            if (!scheduler.HasPendingExternalEffects)
                return;

            await scheduler.WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
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
                {
                    boundaryCursor = new(direction, position.Value);
                    EmitBoundaryReached(position.Value);
                }

                return new(true, position);
            }

            if (!scheduler.HasPendingExternalEffects)
                return default;

            await scheduler.WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryRunOneReady()
    {
        return scheduler.TryRunOneReady();
    }

    private bool HasReady()
    {
        return scheduler.HasReady;
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
        if (checkpoint is not { EntryCheckpoint: not null })
            return false;

        edgeCursor = PlaybackEdgeCursor.None;

        var previousSuppress = suppressImplicitCallContinuationBoundary;
        suppressImplicitCallContinuationBoundary = true;
        PushSuppressCheckpointAutoContinuation();

        try
        {
            RestoreRunnerTreeTo(checkpoint.Value.EntryCheckpoint, false);
            RestoreResumeStoreSnapshot(checkpoint.Value.EntryCheckpoint);
            await RunUntilIdleAsync(currentCancellationToken).ConfigureAwait(false);
            IsCompleted = false;
            SetCurrentRecord(checkpoint.Value.Id);
            return true;
        }
        finally
        {
            PopSuppressCheckpointAutoContinuation();
            suppressImplicitCallContinuationBoundary = previousSuppress;
        }
    }

    private TimelineRecord? FindTerminalEdgeCheckpointRecord()
    {
        for (var i = records.Count - 1; i >= 0; i--)
            if (
                records[i] is
                { Kind: TimelineRecordKind.Checkpoint, EntryCheckpoint: not null } checkpoint
            )
                return checkpoint;

        return null;
    }

    private async ValueTask<bool> TryEvaluatePendingBackwardDelayAtCurrentTimeAsync()
    {
        TimelineRecord? best = null;

        foreach (var record in records)
        {
            if (
                record.Kind != TimelineRecordKind.Delay
                || record.Duration <= TimeSpan.Zero
                || record.StartTime != Time
                || transportStartTime < record.EndTime
            )
                continue;

            if (best == null || record.FlatIndex > best.Value.FlatIndex)
                best = record;
        }

        if (best == null)
            return false;

        await EvaluateRecordAsync(best.Value, Time, PlaybackDirection.Backward)
            .ConfigureAwait(false);
        return true;
    }

    internal void Post<TState>(TState state, Action<TState> action)
        where TState : class
    {
        scheduler.Post(state, action);
    }

    private RecordId NextRecordId()
    {
        return new(++nextRecordId);
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

        TimelineRecord record;

        if (Mode == PlaybackMode.Playback)
        {
            var existing = TryConsumeExistingCheckpointRecord(
                debugLabel,
                CheckpointRecordKind.Entry
            );
            if (existing != null)
            {
                record = existing.Value;
                record.OwnerRunner = runner;
                record.ParentIndex = parentRecord?.FlatIndex;
                record.ParentId = parentRecord?.Id;
                record.Depth = parentRecord == null ? 0 : parentRecord.Value.Depth + 1;
                records[record.FlatIndex] = record;
            }
            else
            {
                SwitchToRecordingFromPlaybackCursor();
                record = TimelineRecord.Checkpoint(
                    NextRecordId(),
                    Time,
                    debugLabel,
                    CheckpointRecordKind.Entry
                );

                record = AddRecord(record, parentRecord, runner);
            }
        }
        else
        {
            record = TimelineRecord.Checkpoint(
                NextRecordId(),
                Time,
                debugLabel,
                CheckpointRecordKind.Entry
            );

            record = AddRecord(record, parentRecord, runner);
        }

        var checkpoint = new TimelineCheckpoint(
            ++checkpointSequence,
            runner,
            0,
            Time,
            PlaybackPromiseKind.Checkpoint,
            null,
            parentRecord?.FlatIndex,
            playbackRecordIndex,
            Timestamp,
            DeltaTime
        );

        SetEntryCheckpoint(record.Id, checkpoint);
    }

    internal RecordId UseCheckpointRecordId(string debugLabel)
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
                return existing.Value.Id;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = TimelineRecord.Checkpoint(
            NextRecordId(),
            Time,
            debugLabel,
            CheckpointRecordKind.User
        );

        return AddRecord(record).Id;
    }

    private TimelineRecord UseImplicitCheckpointRecord(string debugLabel)
    {
        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "ImplicitCheckpoint" : debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            var existing = TryConsumeExistingCheckpointRecord(
                debugLabel,
                CheckpointRecordKind.Implicit
            );
            if (existing != null)
                return existing.Value;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = TimelineRecord.Checkpoint(
            NextRecordId(),
            Time,
            debugLabel,
            CheckpointRecordKind.Implicit
        );

        return AddRecord(record);
    }

    private void CreateOrUpdateImplicitCallContinuationCheckpoint(TimelineRecord call)
    {
        var boundary = UseImplicitCheckpointRecord($"after {call.DebugLabel}");
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
            Timestamp,
            DeltaTime
        );

        // Always update: during playback the runner/checkpoint id can be rebound.
        SetEntryCheckpoint(boundary.Id, checkpoint);
    }

    internal TimelineRecord UseCallRecord(
        IPlaybackRunner parentRunner,
        IPlaybackRunner childRunner,
        string debugLabel
    )
    {
        var label = debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            if (TryConsumeExistingCallRecord(label, parentRunner, childRunner, out var existing))
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var call = TimelineRecord.Call(NextRecordId(), Time, label, parentRunner, childRunner);

        return AddRecord(call);
    }

    internal void BindCallParentAwaitCheckpoint(int callRecordIndex, int checkpointId)
    {
        var call = GetRecord(callRecordIndex);
        if (call.Kind != TimelineRecordKind.Call)
            throw new InvalidOperationException("Call record index does not refer to a call.");

        call.BindParentAwaitCheckpoint(checkpointId);
        records[callRecordIndex] = call;
    }

    internal void OnCheckpointCaptured(
        IPlaybackRunner runner,
        int checkpointId,
        PlaybackPromiseKind awaitKind,
        PlaybackPromiseBase? awaitedPromise,
        int? ownerRecordIndex,
        int? resumeScope
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
            Timestamp,
            DeltaTime
        );

        if (ownerRecordIndex is { } index)
        {
            var ownerRecord = GetRecord(index);
            SetEntryCheckpoint(ownerRecord.Id, checkpoint);

            if (
                SuppressCheckpointAutoContinuation
                && ownerRecord.Kind == TimelineRecordKind.Checkpoint
            )
                pendingForwardCheckpointIndex = ownerRecord.FlatIndex;
        }
        else
        {
            BindStoreCheckpoint(checkpoint);
        }
    }

    internal void OnRunnerCompleted(IPlaybackRunner runner)
    {
        if (runner.CurrentCallRecordIndex is { } callRecordIndex)
        {
            var call = GetRecord(callRecordIndex);
            if (call.Kind != TimelineRecordKind.Call)
                throw new InvalidOperationException("Runner call record is not a call.");
            call.Complete(Time);
            records[callRecordIndex] = call;

            if (
                currentDirection != PlaybackDirection.Backward
                && !suppressImplicitCallContinuationBoundary
                && call.ParentAwaitCheckpointId != 0
            )
                CreateOrUpdateImplicitCallContinuationCheckpoint(call);

            SetCurrentRecord(call.Id);
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

    private void SetEntryCheckpoint(RecordId recordId, TimelineCheckpoint checkpoint)
    {
        ref var target = ref GetRecordRef(GetRecordIndex(recordId));
        var hadEntryCheckpoint = target.EntryCheckpoint != null;
        target.EntryCheckpoint = checkpoint;
        stateStore.SetEntrySnapshot(target.FlatIndex, CaptureStoreSnapshot());
        stateStore.Bind(checkpoint, target.FlatIndex);
        if (target.Kind == TimelineRecordKind.Checkpoint && !hadEntryCheckpoint)
            EmitPlaybackEvent(
                PlaybackEventKind.CheckpointAdded,
                target.Id,
                PlaybackBoundaryKind.Point
            );
    }

    internal void OnRunnerFaulted(IPlaybackRunner runner, Exception exception)
    {
        Console.WriteLine($"EXCEPTION: {exception.GetType().Name}: {exception.Message}");
        OnRunnerCompleted(runner);
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
        PlaybackTransportSource source,
        CancellationToken cancellationToken
    )
    {
        EnsureStarted();
        ResetBoundaryEventDeduplication();
        using var cancellationScope = PushCancellationToken(cancellationToken);

        boundaryCursor = null;

        if (targetTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(targetTime),
                "Target time must be non-negative."
            );

        if (IsFutureTargetBanned(targetTime, source))
            throw new InvalidOperationException("Cannot move beyond the recorded timeline.");

        if (directionOverride == null && targetTime == Time)
        {
            TargetTime = Time;
            return;
        }

        var direction = directionOverride ?? InferTransportDirection(targetTime);
        transportStartTime = Time;

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
            if (edgeEvaluated && targetTime < Time)
                await TryEvaluatePendingBackwardDelayAtCurrentTimeAsync().ConfigureAwait(false);
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
        {
            if (!IsStepBoundaryIncluded(boundary, granularity))
                continue;

            if (direction == PlaybackDirection.Forward)
            {
                if (boundary.Time < Time)
                    continue;

                if (
                    boundary.Time == Time
                    && boundaryCursor
                        is { Direction: PlaybackDirection.Forward, Boundary: var last }
                    && last.Time == Time
                    && boundary.Order <= last.Order
                )
                    continue;

                if (best == null || boundary.CompareTo(best.Value) < 0)
                    best = boundary;

                continue;
            }

            if (boundary.Time > Time)
                continue;

            if (
                boundary.Time == Time
                && boundaryCursor
                    is { Direction: PlaybackDirection.Backward, Boundary: var lastBack }
                && lastBack.Time == Time
                && boundary.Order >= lastBack.Order
            )
                continue;

            if (best == null || boundary.CompareTo(best.Value) > 0)
                best = boundary;
        }

        return best;
    }

    private static TimelineBoundaryScope GetStepBoundaryScope(PlaybackDirection direction)
    {
        return direction == PlaybackDirection.Forward
            ? TimelineBoundaryScope.StepForward
            : TimelineBoundaryScope.StepBackward;
    }

    private bool IsStepBoundaryIncluded(
        TimelineBoundary? boundary,
        PlaybackStepGranularity granularity
    )
    {
        return boundary == null
            ? granularity == PlaybackStepGranularity.AwaitPoint
            : IsStepBoundaryIncluded(boundary.Value, granularity);
    }

    private bool IsStepBoundaryIncluded(
        TimelineBoundary boundary,
        PlaybackStepGranularity granularity
    )
    {
        if (granularity == PlaybackStepGranularity.AwaitPoint)
            return true;

        return GetRecord(boundary.RecordIndex) switch
        {
            { Kind: TimelineRecordKind.Checkpoint } checkpoint => checkpoint.CheckpointKind
                == CheckpointRecordKind.User,
            {
                Kind: TimelineRecordKind.Delay
                    or TimelineRecordKind.Effect
                    or TimelineRecordKind.SeekLoop
            } => true,
            _ => false,
        };
    }

    private IEnumerable<TimelineBoundary> EnumerateTimelineBoundaries(TimelineBoundaryScope scope)
    {
        var timedBoundaryTimes =
            scope == TimelineBoundaryScope.StepBackward ? GetTimedBoundaryTimes() : null;

        foreach (var record in records)
        {
            switch (record.Kind)
            {
                case TimelineRecordKind.Checkpoint:
                    if (scope == TimelineBoundaryScope.Traversal)
                    {
                        yield return TimelineBoundary.Create(record, TimelineBoundaryKind.Point);
                    }
                    else if (
                        scope == TimelineBoundaryScope.StepBackward
                        && IsCheckpointStepBoundary(record, timedBoundaryTimes)
                    )
                    {
                        yield return TimelineBoundary.Create(record, TimelineBoundaryKind.Point);
                    }

                    break;

                case TimelineRecordKind.Delay:
                case TimelineRecordKind.Effect:
                case TimelineRecordKind.SeekLoop:
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

    private TimelineBoundary? GetCurrentBoundaryPosition()
    {
        var record = GetCurrentRecord();
        if (record == null)
            return null;

        switch (record.Value.Kind)
        {
            case TimelineRecordKind.Delay:
            case TimelineRecordKind.Effect:
            case TimelineRecordKind.SeekLoop:
                if (Time == record.Value.StartTime)
                    return TimelineBoundary.Create(record.Value, TimelineBoundaryKind.Start);

                if (record.Value.Duration > TimeSpan.Zero && Time == record.Value.EndTime)
                    return TimelineBoundary.Create(record.Value, TimelineBoundaryKind.End);

                break;

            case TimelineRecordKind.Checkpoint:
                return TimelineBoundary.Create(record.Value, TimelineBoundaryKind.Point);
        }

        return null;
    }

    private bool IsCheckpointStepBoundary(
        TimelineRecord checkpoint,
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
            switch (record.Kind)
            {
                case TimelineRecordKind.Delay:
                case TimelineRecordKind.SeekLoop:
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
            switch (record.Kind)
            {
                case TimelineRecordKind.Delay:
                case TimelineRecordKind.SeekLoop:
                    if (record.StartTime == time)
                        return true;

                    if (record.Duration > TimeSpan.Zero && record.EndTime == time)
                        return true;

                    break;
            }
        }

        return false;
    }

    private bool IsSeekLoopStartBoundary(TimelineBoundary boundary)
    {
        return boundary.Kind == TimelineBoundaryKind.Start
            && GetRecord(boundary.RecordIndex).Kind == TimelineRecordKind.SeekLoop;
    }

    private async ValueTask EvaluateStepBoundaryAsync(
        TimelineBoundary boundary,
        PlaybackDirection direction
    )
    {
        var record = GetRecord(boundary.RecordIndex);
        MoveTimeTo(boundary.Time);

        if (boundary.Kind == TimelineBoundaryKind.Start && record.Kind == TimelineRecordKind.Delay)
        {
            if (direction == PlaybackDirection.Backward)
                RestoreToRecord(record.Id);

            MoveTimeTo(boundary.Time);
            SetCurrentRecord(record.Id);
            EmitBoundaryReached(record.Id, boundary.Kind, boundary.Time);
            return;
        }

        await EvaluateRecordAsync(record, boundary.Time, direction).ConfigureAwait(false);
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
        {
            if (!IsBetween(boundary.Time, from, to, direction))
                continue;

            if (best == null)
            {
                best = boundary;
                continue;
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

        return best;
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
        var evaluatedIds = new HashSet<RecordId>();
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
        HashSet<RecordId> evaluatedIds
    )
    {
        var result = new List<TimelineRecord>();

        foreach (var record in records)
        {
            if (evaluatedIds.Contains(record.Id))
                continue;

            switch (record.Kind)
            {
                case TimelineRecordKind.Delay:
                    if (IsEvaluatableDelayAt(record, time, direction))
                        result.Add(record);
                    break;

                case TimelineRecordKind.Effect:
                    if (record.StartTime == time)
                        result.Add(record);
                    break;

                case TimelineRecordKind.SeekLoop:
                    if (record.Contains(time, includeLoopEnd))
                        result.Add(record);
                    break;

                case TimelineRecordKind.Checkpoint:
                    // Checkpoints are source-segment boundaries. Forward execution
                    // reaches them naturally via RunReady(); direct evaluation is only
                    // needed for backward traversal / target sampling.
                    if (
                        direction == PlaybackDirection.Backward
                        && record.CheckpointKind != CheckpointRecordKind.Implicit
                        && record.StartTime == time
                    )
                        result.Add(record);
                    break;
            }
        }

        if (direction == PlaybackDirection.Forward)
            result.Sort(static (a, b) => a.FlatIndex.CompareTo(b.FlatIndex));
        else
            result.Sort(static (a, b) => b.FlatIndex.CompareTo(a.FlatIndex));

        return result;
    }

    private bool IsEvaluatableDelayAt(
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        if (record.EndTime == time)
            return true;

        if (
            direction != PlaybackDirection.Backward
            || record.Duration <= TimeSpan.Zero
            || transportStartTime < record.EndTime
            || !HasSeekLoopEndingAt(record.StartTime, record.FlatIndex)
        )
            return false;

        return record.StartTime <= time && time < record.EndTime;
    }

    private bool HasSeekLoopEndingAt(TimeSpan time, int beforeRecordIndex)
    {
        for (var i = Math.Min(beforeRecordIndex - 1, records.Count - 1); i >= 0; i--)
        {
            var record = records[i];
            if (record.StartTime < time && record.EndTime < time)
                break;

            if (record.Kind == TimelineRecordKind.SeekLoop && record.EndTime == time)
                return true;
        }

        return false;
    }

    private ValueTask EvaluateRecordAsync(
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        return record.Kind switch
        {
            TimelineRecordKind.Delay => EmitDelayAtAsync(record, time, direction),
            TimelineRecordKind.Effect => EmitEffectAtAsync(record, direction),
            TimelineRecordKind.SeekLoop => EmitSeekLoopAtAsync(record, time, direction),
            TimelineRecordKind.Checkpoint => EmitCheckpointAsync(record, direction),
            _ => ValueTask.CompletedTask,
        };
    }

    private async ValueTask EmitDelayAtAsync(
        TimelineRecord delay,
        TimeSpan targetTime,
        PlaybackDirection direction
    )
    {
        var mustRestore =
            direction == PlaybackDirection.Backward
            || !recordRuntime.HasPendingDelay(delay.FlatIndex);

        if (mustRestore)
            RestoreToRecord(delay.Id);

        MoveTimeTo(targetTime);

        recordRuntime.CompleteDelay(delay.FlatIndex);
        SetCurrentRecord(delay.Id);
        EmitBoundaryReached(delay.Id, ToBoundaryKind(delay, targetTime), targetTime);

        await RunReadyAsync(currentCancellationToken).ConfigureAwait(false);
        EmitCurrentBoundaryReachedAt(targetTime);
    }

    private async ValueTask EmitEffectAtAsync(TimelineRecord effect, PlaybackDirection direction)
    {
        if (direction == PlaybackDirection.Backward)
        {
            MoveTimeTo(effect.StartTime);
            stateStore.RestoreEntry(effect.FlatIndex);
            SetCurrentRecord(effect.Id);
            Mode = PlaybackMode.Playback;
            playbackRecordIndex = Math.Min(effect.FlatIndex + 1, records.Count);
            EmitBoundaryReached(effect.Id, TimelineBoundaryKind.Start, effect.StartTime);
            return;
        }

        RestoreToRecord(effect.Id);
        await RunReadyAsync(currentCancellationToken).ConfigureAwait(false);
        SetCurrentRecord(effect.Id);
        EmitBoundaryReached(effect.Id, TimelineBoundaryKind.Start, effect.StartTime);
    }

    private async ValueTask EmitSeekLoopAtAsync(
        TimelineRecord loop,
        TimeSpan targetTime,
        PlaybackDirection direction
    )
    {
        var active = HasActiveSeekLoop(loop);

        var mustRestore = direction == PlaybackDirection.Backward || !active;

        if (mustRestore)
            RestoreToRecord(loop.Id);

        MoveTimeTo(targetTime);

        if (direction == PlaybackDirection.Backward && targetTime == loop.EndTime)
            suppressLoopExitForIndex = loop.FlatIndex;

        recordRuntime.EmitSeekLoopTrueAt(loop.FlatIndex, loop.StartTime, loop.Duration, targetTime);
        await RunReadyAsync(currentCancellationToken).ConfigureAwait(false);

        SetCurrentRecord(loop.Id);
        EmitBoundaryReached(loop.Id, ToBoundaryKind(loop, targetTime), targetTime);
    }

    private async ValueTask EmitCheckpointAsync(
        TimelineRecord checkpoint,
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
            SetCurrentRecord(checkpoint.Id);

            await RunUntilIdleAsync(currentCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (direction == PlaybackDirection.Backward)
                PopSuppressCheckpointAutoContinuation();

            suppressImplicitCallContinuationBoundary = previousSuppress;
        }

        SetCurrentRecord(checkpoint.Id);
        EmitBoundaryReached(checkpoint.Id, TimelineBoundaryKind.Point, checkpoint.StartTime);
    }

    private bool HasActiveSeekLoop(TimelineRecord loop)
    {
        foreach (var activeIndex in activeSeekLoopIndexes)
            if (
                activeIndex == loop.FlatIndex
                && recordRuntime.HasPendingSeekLoopMoveNext(activeIndex)
            )
                return true;

        return false;
    }

    private void EmitBoundaryReached(
        RecordId recordId,
        TimelineBoundaryKind? boundaryKind,
        TimeSpan eventTime
    )
    {
        if (boundaryKind == null)
            return;

        var publicBoundaryKind = ToPublicBoundaryKind(boundaryKind.Value);
        if (!emittedBoundaries.Add(new(recordId, publicBoundaryKind, currentDirection, eventTime)))
            return;

        EmitPlaybackEvent(
            PlaybackEventKind.BoundaryReached,
            recordId,
            publicBoundaryKind,
            eventTime
        );
    }

    private void EmitBoundaryReached(TimelineBoundary boundary)
    {
        EmitBoundaryReached(GetRecord(boundary.RecordIndex).Id, boundary.Kind, boundary.Time);
    }

    private void EmitTimedRecordStart(TimelineRecord record)
    {
        if (
            record.Kind
            is TimelineRecordKind.Delay
                or TimelineRecordKind.Effect
                or TimelineRecordKind.SeekLoop
        )
            EmitBoundaryReached(record.Id, TimelineBoundaryKind.Start, record.StartTime);
    }

    private void EmitCurrentBoundaryReachedAt(TimeSpan time)
    {
        if (GetCurrentBoundaryPosition() is { } boundary && boundary.Time == time)
            EmitBoundaryReached(boundary);
    }

    private void EmitPlaybackEvent(
        PlaybackEventKind kind,
        RecordId recordId,
        PlaybackBoundaryKind? boundaryKind = null,
        TimeSpan? eventTime = null
    )
    {
        var handler = EventOccurred;
        if (handler == null)
            return;

        var record = GetRecord(recordId).ToInfo();
        handler(
            new(
                kind,
                record,
                boundaryKind,
                currentDirection,
                eventTime ?? Time,
                Timestamp,
                DeltaTime,
                record.DebugLabel
            )
        );
    }

    private void ResetBoundaryEventDeduplication()
    {
        emittedBoundaries.Clear();
    }

    private static TimelineBoundaryKind? ToBoundaryKind(TimelineRecord record, TimeSpan time)
    {
        if (time == record.StartTime)
            return TimelineBoundaryKind.Start;

        if (record.Duration > TimeSpan.Zero && time == record.EndTime)
            return TimelineBoundaryKind.End;

        return null;
    }

    private Exception? StartReplayRevertEffect(Func<CancellationToken, ValueTask> revert)
    {
        var cancellationToken = currentCancellationToken;

        ValueTask task;
        try
        {
            task = revert(cancellationToken);
        }
        catch (Exception exception)
        {
            return exception;
        }

        if (task.IsCompletedSuccessfully)
        {
            task.GetAwaiter().GetResult();
            return null;
        }

        scheduler.BeginExternalEffect();
        _ = CompleteReplayRevertEffectAsync(task);
        return null;
    }

    private async Task CompleteReplayRevertEffectAsync(ValueTask task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch { }
        finally
        {
            scheduler.EndExternalEffect();
        }
    }

    private TimelineRecord UseEffectRecord(
        string debugLabel,
        Func<CancellationToken, ValueTask<object?>> executeAsync
    )
    {
        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "Effect" : debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            if (TryConsumeExistingEffectRecord(debugLabel, out var existing))
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = TimelineRecord.Effect(NextRecordId(), Time, debugLabel, executeAsync);

        return AddRecord(record);
    }

    private TimelineRecord UseDelayRecord(TimeSpan duration, string debugLabel)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration must be non-negative."
            );

        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "Delay" : debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            if (TryConsumeExistingDelayRecord(duration, debugLabel, out var existing))
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = TimelineRecord.Delay(NextRecordId(), Time, duration, debugLabel);

        return AddRecord(record);
    }

    private TimelineRecord UseSeekLoopRecord(TimeSpan duration, string debugLabel)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration must be non-negative."
            );

        debugLabel = string.IsNullOrWhiteSpace(debugLabel) ? "ForEachOnSeek" : debugLabel;

        if (Mode == PlaybackMode.Playback)
        {
            if (TryConsumeExistingSeekLoopRecord(duration, debugLabel, out var existing))
                return existing;

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = TimelineRecord.SeekLoop(NextRecordId(), Time, duration, debugLabel);

        return AddRecord(record);
    }

    private TimelineRecord AddRecord(
        TimelineRecord record,
        TimelineRecord? parentOverride = null,
        IPlaybackRunner? ownerRunnerOverride = null
    )
    {
        record.FlatIndex = records.Count;
        record.OwnerRunner = ownerRunnerOverride ?? PlaybackRuntime.CurrentRunner;

        var currentRunner = PlaybackRuntime.CurrentRunner;
        var scopeIndex = PlaybackRuntime.CurrentRecordScopeIndex;
        TimelineRecord? parent =
            parentOverride
            ?? (
                scopeIndex is { } index && currentRunner != null
                    ? currentRunner[index]
                    : (TimelineRecord?)null
            )
            ?? (
                currentRunner?.CurrentCallRecordIndex is { } callIndex
                    ? currentRunner[callIndex]
                    : (TimelineRecord?)null
            );

        record.ParentIndex = parent?.FlatIndex;
        record.ParentId = parent?.Id;
        record.Depth = parent == null ? 0 : parent.Value.Depth + 1;

        records.Add(record);

        SetCurrentRecord(record.Id);
        playbackRecordIndex = records.Count;
        EmitPlaybackEvent(PlaybackEventKind.RecordAdded, record.Id);
        EmitTimedRecordStart(record);
        return record;
    }

    private TimelineRecord? TryConsumeExistingCheckpointRecord(
        string debugLabel,
        CheckpointRecordKind checkpointKind
    )
    {
        if (playbackRecordIndex >= records.Count)
            return null;

        var checkpoint = records[playbackRecordIndex];
        if (checkpoint.Kind != TimelineRecordKind.Checkpoint)
            return null;

        if (
            checkpoint.StartTime != Time
            || checkpoint.DebugLabel != debugLabel
            || checkpoint.CheckpointKind != checkpointKind
        )
            return null;

        playbackRecordIndex++;
        SetCurrentRecord(checkpoint.Id);
        return checkpoint;
    }

    private bool TryConsumeExistingDelayRecord(
        TimeSpan duration,
        string debugLabel,
        out TimelineRecord record
    )
    {
        record = default;
        if (playbackRecordIndex >= records.Count)
            return false;

        var delay = records[playbackRecordIndex];
        if (delay.Kind != TimelineRecordKind.Delay)
            return false;

        if (delay.StartTime != Time || delay.Duration != duration || delay.DebugLabel != debugLabel)
            return false;

        playbackRecordIndex++;
        SetCurrentRecord(delay.Id);
        EmitTimedRecordStart(delay);
        record = delay;
        return true;
    }

    private bool TryConsumeExistingEffectRecord(string debugLabel, out TimelineRecord record)
    {
        record = default;
        if (playbackRecordIndex >= records.Count)
            return false;

        var effect = records[playbackRecordIndex];
        if (effect.Kind != TimelineRecordKind.Effect)
            return false;

        if (effect.StartTime != Time || effect.DebugLabel != debugLabel)
            return false;

        playbackRecordIndex++;
        SetCurrentRecord(effect.Id);
        EmitTimedRecordStart(effect);
        record = effect;
        return true;
    }

    private bool TryConsumeExistingSeekLoopRecord(
        TimeSpan duration,
        string debugLabel,
        out TimelineRecord record
    )
    {
        record = default;
        if (playbackRecordIndex >= records.Count)
            return false;

        var loop = records[playbackRecordIndex];
        if (loop.Kind != TimelineRecordKind.SeekLoop)
            return false;

        if (loop.StartTime != Time || loop.Duration != duration || loop.DebugLabel != debugLabel)
            return false;

        playbackRecordIndex++;
        SetCurrentRecord(loop.Id);
        EmitTimedRecordStart(loop);
        record = loop;
        return true;
    }

    private bool TryConsumeExistingCallRecord(
        string debugLabel,
        IPlaybackRunner parentRunner,
        IPlaybackRunner childRunner,
        out TimelineRecord record
    )
    {
        record = default;
        if (playbackRecordIndex >= records.Count)
            return false;

        var call = records[playbackRecordIndex];
        if (call.Kind != TimelineRecordKind.Call)
            return false;

        if (call.StartTime != Time || call.DebugLabel != debugLabel)
            return false;

        call.RebindRunners(parentRunner, childRunner);
        records[playbackRecordIndex] = call;

        playbackRecordIndex++;
        SetCurrentRecord(call.Id);
        record = call;
        return true;
    }

    private bool IsFutureTargetBanned(TimeSpan targetTime, PlaybackTransportSource source)
    {
        if (records.Count == 0)
            return false;

        if (targetTime <= GetRecordedEndTime())
            return false;

        if (source == PlaybackTransportSource.Clock && !IsCompleted)
            return false;

        return Mode == PlaybackMode.Playback
            || IsCompleted
            || edgeCursor == PlaybackEdgeCursor.AfterLast;
    }

    private TimeSpan GetRecordedEndTime()
    {
        var end = TimeSpan.Zero;

        foreach (var record in records)
            if (record.EndTime > end)
                end = record.EndTime;

        return end;
    }

    private void SwitchToRecordingFromPlaybackCursor()
    {
        if (Mode == PlaybackMode.Recording)
            return;

        if (currentDirection == PlaybackDirection.Backward)
            throw new InvalidOperationException(
                "Backward replay cannot record a new timeline branch."
            );

        TruncateRecordsFrom(playbackRecordIndex);
        Mode = PlaybackMode.Recording;
    }

    private void TruncateRecordsFrom(int index)
    {
        index = Math.Clamp(index, 0, records.Count);

        for (var i = records.Count - 1; i >= index; i--)
            records.RemoveAt(i);

        nextRecordId = records.Count == 0 ? 0 : records.Max(static record => record.Id.Value);

        if (records.Count == 0)
            SetCurrentRecord((int?)null);
        else
            SetCurrentRecord(records[^1].Id);

        playbackRecordIndex = records.Count;

        RebuildRecordIndexes();
        recordRuntime.TrimTo(records.Count);
    }

    private void RebuildRecordIndexes()
    {
        var liveIndexesById = new Dictionary<RecordId, int>();

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            record.FlatIndex = i;
            liveIndexesById[record.Id] = i;
            records[i] = record;
        }

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];

            if (
                record.ParentId is { } parentId
                && liveIndexesById.TryGetValue(parentId, out var parentIndex)
            )
            {
                record.ParentIndex = parentIndex;
                record.Depth = records[parentIndex].Depth + 1;
                records[i] = record;
                continue;
            }

            record.ParentIndex = null;
            record.ParentId = null;
            record.Depth = 0;
            records[i] = record;
        }
    }

    private void MoveToTimelineGap(TimeSpan targetTime)
    {
        scheduler.ResetReady();
        activeSeekLoopIndexes.Clear();
        recordRuntime.Reset();
        suppressLoopExitForIndex = null;
        pendingForwardCheckpointIndex = null;

        MoveTimeTo(targetTime);
        edgeCursor = PlaybackEdgeCursor.None;

        var nearest = FindNearestRecordAtOrBefore(targetTime);
        if (nearest == null)
            SetCurrentRecord((int?)null);
        else
            SetCurrentRecord(nearest.Value.Id);
        if (nearest == null)
            RestoreStoreSnapshot(null);
        else
            stateStore.RestoreEntry(nearest.Value.FlatIndex);

        Mode = records.Count == 0 ? PlaybackMode.Recording : PlaybackMode.Playback;

        playbackRecordIndex =
            nearest == null ? 0 : Math.Min(nearest.Value.FlatIndex + 1, records.Count);
    }

    private TimelineRecord? FindNearestRecordAtOrBefore(TimeSpan targetTime)
    {
        TimelineRecord? best = null;

        foreach (var record in records)
        {
            if (record.StartTime > targetTime)
                continue;

            if (best == null || record.FlatIndex > best.Value.FlatIndex)
                best = record;
        }

        return best;
    }

    private void RestoreToRecord(RecordId recordId)
    {
        var record = GetRecord(recordId);

        if (record.EntryCheckpoint == null)
        {
            MoveToTimelineGap(record.StartTime);
            return;
        }

        RestoreRunnerTreeTo(record.EntryCheckpoint, true);

        SetCurrentRecord(record.Id);
    }

    private void RestoreRunnerTreeTo(TimelineCheckpoint target, bool reconnectParentContinuations)
    {
        scheduler.ResetReady();
        activeSeekLoopIndexes.Clear();
        suppressLoopExitForIndex = null;
        pendingForwardCheckpointIndex = null;

        RebuildRecordIndexes();
        recordRuntime.Reset();

        playbackRecordIndex = target.RecordCountAtCapture;
        Mode = PlaybackMode.Playback;
        IsCompleted = false;
        edgeCursor = PlaybackEdgeCursor.None;
        stateStore.RestoreEntry(target);

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
        return stateStore.CaptureSnapshot();
    }

    private void RestoreResumeStoreSnapshot(TimelineCheckpoint checkpoint)
    {
        stateStore.RestoreResume(checkpoint);
    }

    private void StartEffect(RecordId recordId, PlaybackPromiseBase promise)
    {
        var record = GetRecord(recordId);
        var cancellationToken = currentCancellationToken;
        var startTimestamp = TimeProvider.GetTimestamp();
        var direction = currentDirection;
        var executeAsync = record.ExecuteAsync;
        scheduler.BeginExternalEffect();

        _ = RunEffectAsync(
            record.Id,
            executeAsync,
            promise,
            startTimestamp,
            direction,
            cancellationToken
        );
    }

    private async Task RunEffectAsync(
        RecordId recordId,
        Func<CancellationToken, ValueTask<object?>> executeAsync,
        PlaybackPromiseBase promise,
        long startTimestamp,
        PlaybackDirection direction,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await executeAsync(cancellationToken).ConfigureAwait(false);
            var endTimestamp = TimeProvider.GetTimestamp();
            Post(
                new PostedEffectCompletion(
                    this,
                    recordId,
                    promise,
                    startTimestamp,
                    endTimestamp,
                    direction,
                    result
                ),
                CompletePostedEffect
            );
        }
        catch (Exception exception)
        {
            var endTimestamp = TimeProvider.GetTimestamp();
            Post(
                new PostedEffectFailure(
                    this,
                    recordId,
                    promise,
                    startTimestamp,
                    endTimestamp,
                    direction,
                    exception
                ),
                FailPostedEffect
            );
        }
        finally
        {
            scheduler.EndExternalEffect();
        }
    }

    private void CompleteEffect(
        RecordId recordId,
        PlaybackPromiseBase promise,
        long startTimestamp,
        long endTimestamp,
        PlaybackDirection direction,
        object? result
    )
    {
        CompleteEffectRecord(recordId, promise, startTimestamp, endTimestamp, direction);
        ref var record = ref GetRecordRef(GetRecordIndex(recordId));
        record.SetEffectResult(result);
        promise.TrySetObjectResult(result);
    }

    private void FailEffect(
        RecordId recordId,
        PlaybackPromiseBase promise,
        long startTimestamp,
        long endTimestamp,
        PlaybackDirection direction,
        Exception exception
    )
    {
        CompleteEffectRecord(recordId, promise, startTimestamp, endTimestamp, direction);
        promise.TrySetException(exception);
    }

    private void CompleteEffectRecord(
        RecordId recordId,
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
            ref var record = ref GetRecordRef(GetRecordIndex(recordId));
            record.Complete(record.StartTime + elapsed);
            promise.Duration = record.Duration;
            promise.DueTime = record.EndTime;
            MoveTimeTo(record.EndTime);
        }
    }

    private void RestoreStoreSnapshot(StoreSnapshot? snapshot)
    {
        stateStore.Restore(snapshot);
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

        scheduler.ResetReady();
        activeSeekLoopIndexes.Clear();
        suppressLoopExitForIndex = null;
        pendingForwardCheckpointIndex = null;

        RebuildRecordIndexes();
        recordRuntime.Reset();

        playbackRecordIndex = GetInitialPlaybackIndex();
        SetCurrentRecord((int?)null);
        IsCompleted = false;
        edgeCursor = postReady ? PlaybackEdgeCursor.None : PlaybackEdgeCursor.BeforeFirst;
        Mode = records.Count == 0 ? PlaybackMode.Recording : PlaybackMode.Playback;
        RestoreStoreSnapshot(null);

        rootRunner.RestoreInitialCheckpoint();

        MoveTimeTo(TimeSpan.Zero);

        if (postReady)
            Post(rootRunner, MoveRunnerNext);
    }

    private int GetInitialPlaybackIndex()
    {
        if (records.Count == 0)
            return 0;

        if (
            rootRunner != null
            && records[0].Kind == TimelineRecordKind.Checkpoint
            && ReferenceEquals(records[0].OwnerRunner, rootRunner)
            && records[0].StartTime == TimeSpan.Zero
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

                Post(
                    new PostedCheckpointResume(
                        checkpoint.Runner,
                        checkpoint.CheckpointId,
                        expectedEpoch,
                        resumeScope
                    ),
                    ResumeCheckpoint
                );

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

                Post(promise, CompletePromise);
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
                    promise.OwnerRecordIndex is { } delayIndex
                    && GetRecord(delayIndex) is { Kind: TimelineRecordKind.Delay } delayRecord
                        ? delayRecord
                        : throw new InvalidOperationException(
                            "Delay checkpoint has no delay record."
                        );

                recordRuntime.ArmDelay(delay.FlatIndex, promise);

                if (delay.Duration == TimeSpan.Zero)
                    Post(new PostedDelayCompletion(this, delay.FlatIndex), CompleteDelay);

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
                    promise.OwnerRecordIndex is { } effectIndex
                    && GetRecord(effectIndex) is { Kind: TimelineRecordKind.Effect } effectRecord
                        ? effectRecord
                        : throw new InvalidOperationException(
                            "Effect checkpoint has no effect record."
                        );

                if (currentDirection == PlaybackDirection.Backward)
                {
                    effect.TryGetEffectResult(out var result);
                    promise.TrySetObjectResult(result);
                    break;
                }

                StartEffect(effect.Id, promise);
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

                TimelineRecord? ownerRecord = promise.OwnerRecordIndex is { } ownerIndex
                    ? GetRecord(ownerIndex)
                    : (TimelineRecord?)null;

                switch (ownerRecord?.Kind)
                {
                    case TimelineRecordKind.SeekLoop:
                        var loop = ownerRecord.Value;
                        recordRuntime.ArmSeekLoopMoveNext(loop.FlatIndex, promise);

                        if (!activeSeekLoopIndexes.Contains(loop.FlatIndex))
                            activeSeekLoopIndexes.Add(loop.FlatIndex);

                        break;

                    case TimelineRecordKind.Checkpoint:
                        // This is the implicit await-foreach exit checkpoint.
                        // Resume the state machine with MoveNextAsync() == false.
                        Post(promise, CompleteBoolPromiseWithFalse);
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
            boundary is { } value ? GetRecord(value.RecordIndex).ToInfo() : null,
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

    private static TransportEvaluation ToTransportEvaluation(PlaybackMoveMode mode)
    {
        return mode switch
        {
            PlaybackMoveMode.TargetOnly => TransportEvaluation.TargetOnly,
            PlaybackMoveMode.Traverse => TransportEvaluation.Traverse,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    private static PlaybackMoveMode ToMoveMode(TransportEvaluation evaluation)
    {
        return evaluation switch
        {
            TransportEvaluation.TargetOnly => PlaybackMoveMode.TargetOnly,
            TransportEvaluation.Traverse => PlaybackMoveMode.Traverse,
            _ => throw new ArgumentOutOfRangeException(nameof(evaluation), evaluation, null),
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

    private sealed record PostedEffectCompletion(
        Playback Playback,
        RecordId RecordId,
        PlaybackPromiseBase Promise,
        long StartTimestamp,
        long EndTimestamp,
        PlaybackDirection Direction,
        object? Result
    );

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
