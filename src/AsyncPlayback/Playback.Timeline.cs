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
                playbackRecordIndex < records.Count
                && records[playbackRecordIndex]
                    .Behavior.IsReplayMatch(records[playbackRecordIndex], request)
            )
            {
                var existing = records[playbackRecordIndex];
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
        if (playbackRecordIndex >= records.Count)
            return false;

        var delay = records[playbackRecordIndex];
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
        if (playbackRecordIndex >= records.Count)
            return false;

        var effect = records[playbackRecordIndex];
        var request = new TimelineRecordCreateRequest(
            new EffectRecordBehavior(_ => ValueTask.FromResult<object?>(null)),
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
        if (playbackRecordIndex >= records.Count)
            return false;

        var loop = records[playbackRecordIndex];
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
        if (playbackRecordIndex >= records.Count)
            return false;

        var call = records[playbackRecordIndex];
        var request = new TimelineRecordCreateRequest(
            new CallRecordBehavior(parentRunner, childRunner),
            Time,
            TimeSpan.Zero,
            debugLabel
        );

        if (!call.Behavior.IsReplayMatch(call, request))
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
}
