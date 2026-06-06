namespace AsyncPlayback;

public sealed partial class Playback
{
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
            && GetRecord(pendingCheckpointIndex) is { EntryCheckpoint: not null } checkpoint
            && checkpoint.Behavior is TimelineRecordBehavior checkpointType
            && checkpointType.IsCheckpointRecord(checkpoint)
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

    internal async ValueTask RunReadyAsync(CancellationToken cancellationToken = default)
    {
        while (TryRunOneReady())
            await Task.Yield();
    }

    internal async ValueTask RunUntilIdleAsync(CancellationToken cancellationToken = default)
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
        for (var i = timeline.Count - 1; i >= 0; i--)
            if (
                timeline[i] is { EntryCheckpoint: not null } checkpoint
                && checkpoint.Behavior is TimelineRecordBehavior checkpointType
                && checkpointType.IsCheckpointRecord(checkpoint)
            )
                return checkpoint;

        return null;
    }

    private async ValueTask<bool> TryEvaluatePendingBackwardDelayAtCurrentTimeAsync()
    {
        TimelineRecord? best = null;

        for (var i = 0; i < timeline.Count; i++)
        {
            var record = timeline[i];
            if (
                record.Behavior is not TimelineRecordBehavior builtIn
                || !builtIn.IsPendingBackwardDelayCandidate(record, Time, transportStartTime)
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
}
