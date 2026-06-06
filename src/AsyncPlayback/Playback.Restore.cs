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
            && records[0].Behavior is TimelineRecordBehavior builtIn
            && builtIn.IsInitialPlaybackCheckpoint(records[0], rootRunner)
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
                    && GetRecord(delayIndex) is var delayRecord
                    && delayRecord.Behavior is TimelineRecordBehavior delayType
                    && delayType.IsDelayRecord(delayRecord)
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
                    && GetRecord(effectIndex) is { Behavior: EffectRecordBehavior } effectRecord
                        ? effectRecord
                        : throw new InvalidOperationException(
                            "Effect checkpoint has no effect record."
                        );

                if (currentDirection == PlaybackDirection.Backward)
                {
                    effect.EffectBehavior.ReplayResult(promise);
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

                if (
                    ownerRecord is { } loopOwner
                    && loopOwner.Behavior is TimelineRecordBehavior loopType
                    && loopType.IsSeekLoopRecord(loopOwner)
                )
                {
                    recordRuntime.ArmSeekLoopMoveNext(loopOwner.FlatIndex, promise);

                    if (!activeSeekLoopIndexes.Contains(loopOwner.FlatIndex))
                        activeSeekLoopIndexes.Add(loopOwner.FlatIndex);
                }
                else if (
                    ownerRecord is { } checkpointOwner
                    && checkpointOwner.Behavior is TimelineRecordBehavior checkpointType
                    && checkpointType.IsCheckpointRecord(checkpointOwner)
                )
                {
                    // This is the implicit await-foreach exit checkpoint.
                    // Resume the state machine with MoveNextAsync() == false.
                    Post(promise, CompleteBoolPromiseWithFalse);
                }
                else
                {
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

    private static long AbsTicks(TimeSpan value)
    {
        return value.Ticks == long.MinValue ? long.MaxValue : Math.Abs(value.Ticks);
    }
}
