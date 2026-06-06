namespace MinimumPlayback;

public sealed class Playback
{
    private PlaybackRecord[] records = [];
    private IPlaybackRunner?[] recordRunners = [];
    private int[] checkpointIdsByRecord = [];
    private int recordCount;
    private IPlaybackRunner? rootRunner;
    private int cursor = -1;
    private int? rewriteFrom;

    public ReadOnlySpan<PlaybackRecord> Records => records.AsSpan(0, recordCount);
    public PlaybackRecord? Current { get; private set; }
    public bool IsCompleted { get; private set; }

    public static Playback Start(Func<Playback, PlaybackTask> entry)
    {
        var playback = new Playback();
        playback.StartCore(entry);
        return playback;
    }

    public CheckpointAwaitable Checkpoint(string label = "Checkpoint")
    {
        if (!ReferenceEquals(PlaybackRuntime.CurrentPlayback, this))
            throw new InvalidOperationException("Checkpoint must be awaited inside this playback.");

        return new(this, string.IsNullOrWhiteSpace(label) ? "Checkpoint" : label);
    }

    public bool TryMoveNext()
    {
        if (rewriteFrom != null)
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
        if (cursor < 0)
            return false;

        var previous = FindPreviousCheckpoint(cursor);
        rewriteFrom = previous + 1;
        cursor = previous;
        Current = null;
        IsCompleted = false;
        return true;
    }

    internal void AttachRootRunner(IPlaybackRunner runner)
    {
        rootRunner ??= runner;
    }

    internal int AddCheckpoint(IPlaybackRunner runner, int checkpointId, string label)
    {
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
        TruncateIfRewriting();
        return AddRecord(
            PlaybackRecordRole.Call,
            label,
            parent.Depth + 1,
            parent.CurrentRecordIndex ?? -1
        ).Index;
    }

    internal void OnRootCompleted()
    {
        IsCompleted = true;
    }

    private void StartCore(Func<Playback, PlaybackTask> entry)
    {
        using var scope = PlaybackRuntime.Push(this, null);
        _ = entry(this);
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

    private void TruncateIfRewriting()
    {
        if (rewriteFrom is not { } index)
            return;

        if (index < recordCount)
        {
            Array.Clear(records, index, recordCount - index);
            Array.Clear(recordRunners, index, recordCount - index);
            Array.Clear(checkpointIdsByRecord, index, recordCount - index);
            recordCount = index;
        }
        rewriteFrom = null;
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
}
