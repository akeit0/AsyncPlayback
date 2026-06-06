namespace AsyncPlayback;

public sealed partial class Playback
{
    internal void RestoreToRecord(RecordId recordId)
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

    internal void RestoreRunnerTreeTo(TimelineCheckpoint target, bool reconnectParentContinuations)
    {
        scheduler.ResetReady();
        activeSeekLoopIndexes.Clear();
        suppressLoopExitForIndex = null;
        pendingForwardCheckpointIndex = null;

        timeline.RebuildRecordIndexes();
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

    internal void RestoreResumeStoreSnapshot(TimelineCheckpoint checkpoint)
    {
        stateStore.RestoreResume(checkpoint);
    }

    private void StartEffect(RecordId recordId, PlaybackPromiseBase promise)
    {
        var record = GetRecord(recordId);
        var cancellationToken = currentCancellationToken;
        var startTimestamp = TimeProvider.GetTimestamp();
        var direction = currentDirection;
        scheduler.BeginExternalEffect();

        _ = record.EffectBehavior.RunAsync(
            this,
            record.Id,
            promise,
            startTimestamp,
            direction,
            cancellationToken
        );
    }

    internal void PostEffectFailure(
        RecordId recordId,
        PlaybackPromiseBase promise,
        long startTimestamp,
        PlaybackDirection direction,
        Exception exception
    )
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

    internal void EndExternalEffect()
    {
        scheduler.EndExternalEffect();
    }

    internal void FailEffect(
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

    internal void CompleteEffectRecord(
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
            timeline.Invalidate();
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

        timeline.RebuildRecordIndexes();
        recordRuntime.Reset();

        playbackRecordIndex = GetInitialPlaybackIndex();
        SetCurrentRecord((int?)null);
        IsCompleted = false;
        edgeCursor = postReady ? PlaybackEdgeCursor.None : PlaybackEdgeCursor.BeforeFirst;
        Mode = timeline.Count == 0 ? PlaybackMode.Recording : PlaybackMode.Playback;
        RestoreStoreSnapshot(null);

        rootRunner.RestoreInitialCheckpoint();

        MoveTimeTo(TimeSpan.Zero);

        if (postReady)
            Post(rootRunner, MoveRunnerNext);
    }

    private int GetInitialPlaybackIndex()
    {
        if (timeline.Count == 0)
            return 0;

        if (
            rootRunner != null
            && timeline[0].Behavior is TimelineRecordBehavior builtIn
            && builtIn.IsInitialPlaybackCheckpoint(timeline[0], rootRunner)
        )
            return 1;

        return 0;
    }

    private void ArmCheckpoint(TimelineCheckpoint checkpoint)
    {
        checkpoint.AwaitBehavior.Arm(this, checkpoint);
    }

    internal void MoveTimeTo(TimeSpan time)
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
}
