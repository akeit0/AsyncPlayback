namespace AsyncPlayback;

public sealed partial class Playback
{
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
                timeline[record.FlatIndex] = record;
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
            CheckpointAwaitBehavior.Instance,
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
            CheckpointAwaitBehavior.Instance,
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
        if (call.Behavior is not CallRecordBehavior)
            throw new InvalidOperationException("Call record index does not refer to a call.");

        call.BindParentAwaitCheckpoint(checkpointId);
        timeline[callRecordIndex] = call;
    }

    internal void OnCheckpointCaptured(
        IPlaybackRunner runner,
        int checkpointId,
        IPlaybackAwaitBehavior awaitBehavior,
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
            awaitBehavior,
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
                && ownerRecord.Behavior is TimelineRecordBehavior ownerType
                && ownerType.SuppressesCheckpointAutoContinuation(ownerRecord)
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
            if (call.Behavior is not CallRecordBehavior)
                throw new InvalidOperationException("Runner call record is not a call.");
            call.Complete(Time);
            timeline[callRecordIndex] = call;

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
        timeline.Invalidate();
        stateStore.SetEntrySnapshot(target.FlatIndex, CaptureStoreSnapshot());
        stateStore.Bind(checkpoint, target.FlatIndex);
        if (
            target.Behavior is TimelineRecordBehavior targetType
            && targetType.ShouldEmitCheckpointAdded(target, hadEntryCheckpoint)
        )
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
}
