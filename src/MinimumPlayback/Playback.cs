namespace MinimumPlayback;

public sealed class Playback
{
    private PlaybackRecord[] records = [];
    private IPlaybackRunner?[] recordRunners = [];
    private int[] checkpointIdsByRecord = [];
    private int recordCount;
    private IPlaybackRunner? rootRunner;
    private Func<Playback, PlaybackTask>? entry;
    private bool started;
    private int cursor = -1;
    private PlaybackMode mode;
    private int rewriteFrom = -1;
    private int replayConsumeIndex = -1;
    private int replayStopIndex = -1;

    public ReadOnlySpan<PlaybackRecord> Records => records.AsSpan(0, recordCount);
    public PlaybackRecord? Current { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsForward { get; private set; } = true;

    public static Playback Create(Func<Playback, PlaybackTask> entry)
    {
        return new() { entry = entry };
    }

    public CheckpointAwaitable Checkpoint(string label = "Checkpoint")
    {
        if (!ReferenceEquals(PlaybackRuntime.CurrentPlayback, this))
            throw new InvalidOperationException("Checkpoint must be awaited inside this playback.");

        return new(this, string.IsNullOrWhiteSpace(label) ? "Checkpoint" : label);
    }

    public bool TryMoveNext()
    {
        IsForward = true;
        EnsureStarted();
        if (mode == PlaybackMode.Rewriting)
            return RewriteNext();

        var next = FindNextCheckpoint(cursor);
        if (next < 0 && !TryRecordNext())
            return false;

        next = FindNextCheckpoint(cursor);
        if (next < 0)
            return false;

        cursor = next;
        Current = records[next];
        return true;
    }

    public bool TryMoveBack()
    {
        EnsureStarted();

        if (cursor < 0)
            return false;
        IsForward = false;
        var target = cursor;
        var previous = FindPreviousCheckpoint(cursor);
        IsCompleted = false;
        ReplayExisting(previous, target);
        mode = PlaybackMode.Rewriting;
        rewriteFrom = previous + 1;
        cursor = previous;
        Current = null;
        return true;
    }

    internal void AttachRootRunner(IPlaybackRunner runner)
    {
        rootRunner ??= runner;
    }

    internal int AddCheckpoint(IPlaybackRunner runner, int checkpointId, string label)
    {
        if (TryConsumeReplayRecord(PlaybackRecordRole.Checkpoint, label, out var replayRecord))
        {
            recordRunners[replayRecord.Index] = runner;
            checkpointIdsByRecord[replayRecord.Index] = checkpointId + 1;
            return replayRecord.Index;
        }

        TruncateIfRewriting();
        var record = AddRecord(
            PlaybackRecordRole.Checkpoint,
            label,
            runner.Depth,
            runner.CallRecordIndex ?? -1
        );
        recordRunners[record.Index] = runner;
        checkpointIdsByRecord[record.Index] = checkpointId + 1;
        return record.Index;
    }

    internal int AddCall(IPlaybackRunner parent, string label)
    {
        if (TryConsumeReplayRecord(PlaybackRecordRole.Call, label, out var replayRecord))
            return replayRecord.Index;

        TruncateIfRewriting();
        return AddRecord(
            PlaybackRecordRole.Call,
            label,
            parent.Depth + 1,
            GetDebugParentIndex(parent)
        ).Index;
    }

    internal void OnRootCompleted()
    {
        IsCompleted = true;
    }

    private void StartCore(Func<Playback, PlaybackTask> entry)
    {
        this.entry = entry;
        started = true;
        using var scope = PlaybackRuntime.Push(this, null);
        _ = entry(this);
    }

    private void EnsureStarted()
    {
        if (started)
            return;

        if (entry == null)
            throw new InvalidOperationException("Playback has no entry.");

        StartCore(entry);
    }

    private void ReplayExisting(int startIndex, int stopIndex)
    {
        mode = PlaybackMode.Replaying;
        replayConsumeIndex = startIndex + 1;
        replayStopIndex = stopIndex;

        IPlaybackRunner runner;
        if (startIndex < 0)
        {
            runner = rootRunner!;
            rootRunner!.RestoreInitial();
        }
        else
        {
            runner = Restore(records[startIndex]);
        }

        runner.MoveNext();

        if (mode == PlaybackMode.Replaying)
            throw new InvalidOperationException("Replay did not reach the expected checkpoint.");
    }

    private bool RewriteNext()
    {
        TruncateIfRewriting();

        IPlaybackRunner runner;
        if (cursor < 0)
        {
            runner = rootRunner!;
            rootRunner!.RestoreInitial();
        }
        else
        {
            runner = Restore(records[cursor]);
        }

        runner.MoveNext();

        var next = FindNextCheckpoint(cursor);
        if (next < 0)
            return false;

        cursor = next;
        Current = records[next];
        return true;
    }

    private bool TryRecordNext()
    {
        if (IsCompleted)
            return false;

        IPlaybackRunner runner;
        if (cursor < 0)
        {
            runner = rootRunner!;
            rootRunner!.RestoreInitial();
        }
        else
        {
            runner = Restore(records[cursor]);
        }

        runner.MoveNext();
        return true;
    }

    private IPlaybackRunner Restore(PlaybackRecord record)
    {
        var runner = GetRunner(record);
        var checkpointKey = checkpointIdsByRecord[record.Index];
        if (checkpointKey == 0)
            throw new InvalidOperationException("Record checkpoint was not found.");

        runner.RestoreCheckpoint(checkpointKey - 1, record.Index);
        return runner;
    }

    private IPlaybackRunner GetRunner(PlaybackRecord record)
    {
        if (rootRunner == null)
            throw new InvalidOperationException("Playback has no root runner.");

        return recordRunners[record.Index]
            ?? throw new InvalidOperationException("Record runner was not found.");
    }

    private PlaybackRecord AddRecord(
        PlaybackRecordRole role,
        string label,
        int depth,
        int parentIndex
    )
    {
        EnsureRecordCapacity();
        var record = new PlaybackRecord(recordCount, role, label, depth, parentIndex);
        records[recordCount++] = record;
        return record;
    }

    private bool TryConsumeReplayRecord(
        PlaybackRecordRole role,
        string label,
        out PlaybackRecord record
    )
    {
        if (mode != PlaybackMode.Replaying)
        {
            record = default;
            return false;
        }

        if (replayConsumeIndex < 0 || replayConsumeIndex > replayStopIndex)
            throw new InvalidOperationException("Replay consumed past the expected checkpoint.");

        record = records[replayConsumeIndex++];
        if (record.Role != role || record.Label != label)
            throw new InvalidOperationException(
                "Replay record does not match the existing timeline."
            );

        if (record.Index == replayStopIndex)
        {
            replayConsumeIndex = -1;
            replayStopIndex = -1;
            mode = PlaybackMode.Normal;
        }

        return true;
    }

    private void TruncateIfRewriting()
    {
        if (mode != PlaybackMode.Rewriting)
            return;

        var index = rewriteFrom;
        if (index < recordCount)
        {
            Array.Clear(records, index, recordCount - index);
            Array.Clear(recordRunners, index, recordCount - index);
            Array.Clear(checkpointIdsByRecord, index, recordCount - index);
            recordCount = index;
        }
        rewriteFrom = -1;
        mode = PlaybackMode.Normal;
    }

    private static int GetDebugParentIndex(IPlaybackRunner parent)
    {
        return parent.CurrentRecordIndex ?? parent.CallRecordIndex ?? -1;
    }

    private int FindNextCheckpoint(int afterIndex)
    {
        for (var i = afterIndex + 1; i < recordCount; i++)
            if (records[i].Role == PlaybackRecordRole.Checkpoint)
                return i;

        return -1;
    }

    private int FindPreviousCheckpoint(int beforeIndex)
    {
        for (var i = beforeIndex - 1; i >= 0; i--)
            if (records[i].Role == PlaybackRecordRole.Checkpoint)
                return i;

        return -1;
    }

    private void EnsureRecordCapacity()
    {
        if (recordCount < records.Length)
            return;

        var capacity = records.Length == 0 ? 8 : records.Length * 2;
        Array.Resize(ref records, capacity);
        Array.Resize(ref recordRunners, capacity);
        Array.Resize(ref checkpointIdsByRecord, capacity);
    }

    private enum PlaybackMode
    {
        Normal,
        Rewriting,
        Replaying,
    }
}
