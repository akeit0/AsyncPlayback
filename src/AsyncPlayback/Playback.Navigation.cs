namespace AsyncPlayback;

public sealed partial class Playback
{
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

        var record = GetRecord(boundary.RecordIndex);
        return record.Behavior is TimelineRecordBehavior builtIn
            ? builtIn.IsStepBoundaryIncluded(record, boundary, granularity)
            : record.Behavior.GetVisibility(record) == TimelineRecordVisibility.Workflow;
    }

    private IEnumerable<TimelineBoundary> EnumerateTimelineBoundaries(TimelineBoundaryScope scope)
    {
        var timedBoundaryTimes =
            scope == TimelineBoundaryScope.StepBackward ? GetTimedBoundaryTimes() : null;
        var boundaries = new List<TimelineBoundary>();
        var builder = new TimelineBoundaryBuilder(this, scope, timedBoundaryTimes, boundaries);

        foreach (var record in records)
        {
            boundaries.Clear();
            record.Behavior.AddBoundaries(record, builder);

            foreach (var boundary in boundaries)
                yield return boundary;
        }
    }

    private TimelineBoundary? GetCurrentBoundaryPosition()
    {
        var record = GetCurrentRecord();
        if (record == null)
            return null;

        return record.Value.Behavior is TimelineRecordBehavior builtIn
            ? builtIn.GetCurrentBoundaryPosition(record.Value, Time)
            : null;
    }

    internal bool IsCheckpointStepBoundary(
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
            if (record.Behavior is TimelineRecordBehavior builtIn)
                builtIn.AddTimedBoundaryTimes(record, result);
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
            if (
                record.Behavior is TimelineRecordBehavior builtIn
                && builtIn.HasTimedBoundaryAt(record, time)
            )
                return true;
        }

        return false;
    }

    private bool IsSeekLoopStartBoundary(TimelineBoundary boundary)
    {
        return boundary.Kind == TimelineBoundaryKind.Start
            && GetRecord(boundary.RecordIndex).Behavior is TimelineRecordBehavior builtIn
            && builtIn.IsSeekLoopStartBoundary(GetRecord(boundary.RecordIndex), boundary.Kind);
    }

    private async ValueTask EvaluateStepBoundaryAsync(
        TimelineBoundary boundary,
        PlaybackDirection direction
    )
    {
        var record = GetRecord(boundary.RecordIndex);
        MoveTimeTo(boundary.Time);

        if (
            boundary.Kind == TimelineBoundaryKind.Start
            && record.Behavior is TimelineRecordBehavior builtIn
            && builtIn.IsDelayStartBoundary(record, boundary.Kind)
        )
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
}
