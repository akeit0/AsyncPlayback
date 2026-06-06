namespace AsyncPlayback;

public sealed partial class Playback
{
    public TimelineRecordInfo AddRecord(
        ITimelineRecordBehavior behavior,
        TimeSpan duration = default,
        string? debugLabel = null
    )
    {
        EnsureCurrentPlayback();

        if (behavior == null)
            throw new ArgumentNullException(nameof(behavior));

        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration must be non-negative."
            );

        var label = string.IsNullOrWhiteSpace(debugLabel) ? behavior.TypeName : debugLabel;
        var request = new TimelineRecordCreateRequest(behavior, Time, duration, label);

        if (Mode == PlaybackMode.Playback)
        {
            if (
                playbackRecordIndex < timeline.Count
                && timeline[playbackRecordIndex]
                    .Behavior.IsReplayMatch(timeline[playbackRecordIndex], request)
            )
            {
                var existing = timeline[playbackRecordIndex];
                playbackRecordIndex++;
                SetCurrentRecord(existing.Id);
                return existing.ToInfo();
            }

            SwitchToRecordingFromPlaybackCursor();
        }

        var record = TimelineRecord.Create(NextRecordId(), behavior, Time, duration, label);
        return AddRecord(record).ToInfo();
    }

    private TimelineRecord UseEffectRecord(
        string debugLabel,
        Func<CancellationToken, ValueTask> executeAsync
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

    private TimelineRecord UseEffectRecord<T>(
        string debugLabel,
        Func<CancellationToken, ValueTask<T>> executeAsync
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
        record.FlatIndex = timeline.Count;
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

        timeline.Add(record);

        SetCurrentRecord(record.Id);
        playbackRecordIndex = timeline.Count;
        EmitPlaybackEvent(PlaybackEventKind.RecordAdded, record.Id);
        EmitTimedRecordStart(record);
        return record;
    }

    private TimelineRecord? TryConsumeExistingCheckpointRecord(
        string debugLabel,
        CheckpointRecordKind checkpointKind
    )
    {
        if (playbackRecordIndex >= timeline.Count)
            return null;

        var checkpoint = timeline[playbackRecordIndex];
        var request = new TimelineRecordCreateRequest(
            new CheckpointRecordBehavior(checkpointKind),
            Time,
            TimeSpan.Zero,
            debugLabel
        );

        if (!checkpoint.Behavior.IsReplayMatch(checkpoint, request))
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
        if (playbackRecordIndex >= timeline.Count)
            return false;

        var delay = timeline[playbackRecordIndex];
        var request = new TimelineRecordCreateRequest(
            new DelayRecordBehavior(),
            Time,
            duration,
            debugLabel
        );

        if (!delay.Behavior.IsReplayMatch(delay, request))
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
        if (playbackRecordIndex >= timeline.Count)
            return false;

        var effect = timeline[playbackRecordIndex];
        var request = new TimelineRecordCreateRequest(
            new VoidEffectRecordBehavior(_ => ValueTask.CompletedTask),
            Time,
            TimeSpan.Zero,
            debugLabel
        );

        if (!effect.Behavior.IsReplayMatch(effect, request))
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
        if (playbackRecordIndex >= timeline.Count)
            return false;

        var loop = timeline[playbackRecordIndex];
        var request = new TimelineRecordCreateRequest(
            new SeekLoopRecordBehavior(),
            Time,
            duration,
            debugLabel
        );

        if (!loop.Behavior.IsReplayMatch(loop, request))
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
        if (playbackRecordIndex >= timeline.Count)
            return false;

        var call = timeline[playbackRecordIndex];
        var request = new TimelineRecordCreateRequest(
            new CallRecordBehavior(parentRunner, childRunner),
            Time,
            TimeSpan.Zero,
            debugLabel
        );

        if (!call.Behavior.IsReplayMatch(call, request))
            return false;

        call.RebindRunners(parentRunner, childRunner);
        timeline[playbackRecordIndex] = call;

        playbackRecordIndex++;
        SetCurrentRecord(call.Id);
        record = call;
        return true;
    }

    private bool IsFutureTargetBanned(TimeSpan targetTime, PlaybackTransportSource source)
    {
        if (timeline.Count == 0)
            return false;

        if (targetTime <= timeline.RecordedEndTime)
            return false;

        if (source == PlaybackTransportSource.Clock && !IsCompleted)
            return false;

        return Mode == PlaybackMode.Playback
            || IsCompleted
            || edgeCursor == PlaybackEdgeCursor.AfterLast;
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
        timeline.TruncateFrom(index);
        nextRecordId = timeline.NextIdSeed;

        if (timeline.Count == 0)
            SetCurrentRecord((int?)null);
        else
            SetCurrentRecord(timeline.Last!.Value.Id);

        playbackRecordIndex = timeline.Count;

        recordRuntime.TrimTo(timeline.Count);
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

        var nearest = timeline.FindNearestRecordAtOrBefore(targetTime);
        if (nearest == null)
            SetCurrentRecord((int?)null);
        else
            SetCurrentRecord(nearest.Value.Id);
        if (nearest == null)
            RestoreStoreSnapshot(null);
        else
            stateStore.RestoreEntry(nearest.Value.FlatIndex);

        Mode = timeline.Count == 0 ? PlaybackMode.Recording : PlaybackMode.Playback;

        playbackRecordIndex =
            nearest == null ? 0 : Math.Min(nearest.Value.FlatIndex + 1, timeline.Count);
    }
}
