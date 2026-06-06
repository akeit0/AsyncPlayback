namespace AsyncPlayback;

public sealed partial class Playback
{
    internal bool HasSeekLoopEndingAt(TimeSpan time, int beforeRecordIndex)
    {
        return timeline.HasSeekLoopEndingAt(time, beforeRecordIndex);
    }

    internal bool HasPendingDelay(int recordIndex)
    {
        return recordRuntime.HasPendingDelay(recordIndex);
    }

    internal void CompleteDelayRecord(int recordIndex)
    {
        recordRuntime.CompleteDelay(recordIndex);
    }

    internal void RestoreEntryState(int recordIndex)
    {
        stateStore.RestoreEntry(recordIndex);
    }

    internal void EnterPlaybackAfterRecord(int recordIndex)
    {
        Mode = PlaybackMode.Playback;
        playbackRecordIndex = Math.Min(recordIndex + 1, timeline.Count);
    }

    internal void SuppressLoopExitForRecord(int recordIndex)
    {
        suppressLoopExitForIndex = recordIndex;
    }

    internal void EmitSeekLoopTrueAt(TimelineRecord loop, TimeSpan targetTime)
    {
        recordRuntime.EmitSeekLoopTrueAt(loop.FlatIndex, loop.StartTime, loop.Duration, targetTime);
    }

    internal async ValueTask EvaluateCheckpointRecordAsync(
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

    internal bool HasActiveSeekLoop(TimelineRecord loop)
    {
        foreach (var activeIndex in activeSeekLoopIndexes)
            if (
                activeIndex == loop.FlatIndex
                && recordRuntime.HasPendingSeekLoopMoveNext(activeIndex)
            )
                return true;

        return false;
    }

    internal void EmitBoundaryReached(
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

    public void MarkRecordEvaluated(
        TimelineRecord record,
        PlaybackBoundaryKind? boundaryKind,
        TimeSpan time
    )
    {
        MoveTimeTo(time);
        SetCurrentRecord(record.Id);

        if (boundaryKind is { } publicKind)
            EmitPlaybackEvent(PlaybackEventKind.BoundaryReached, record.Id, publicKind, time);
    }

    private void EmitTimedRecordStart(TimelineRecord record)
    {
        if (
            record.Behavior is TimelineRecordBehavior builtIn
            && builtIn.ShouldEmitTimedRecordStart(record)
        )
            EmitBoundaryReached(record.Id, TimelineBoundaryKind.Start, record.StartTime);
    }

    internal void EmitCurrentBoundaryReachedAt(TimeSpan time)
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

    internal static TimelineBoundaryKind? ToBoundaryKind(TimelineRecord record, TimeSpan time)
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
}
