namespace MinimumPlayback;

public sealed class Playback
{
    private const int NoStopId = -1;

    private PlaybackRecord[] records = [];
    private IPlaybackRunner?[] recordRunners = [];
    private int[] stopIdsByRecord = [];
    private int recordCount;
    private IPlaybackRunner? rootRunner;
    private Func<Playback, PlaybackTask>? entry;
    private bool started;
    private int cursor = -1;
    private PlaybackMode mode;
    private int rewriteFrom = -1;
    private int replayConsumeIndex = -1;
    private int replayStopIndex = -1;
    public int Cursor => cursor;

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

        if (!TryRecordNext())
            return false;

        var next = FindNextStop(cursor);
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
        var previous = FindPreviousStop(cursor);
        ReplayExisting(previous, target);
        IsCompleted = false;
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

    internal int AddCheckpoint(IPlaybackRunner runner, int stopId, string label)
    {
        if (TryConsumeReplayRecord(PlaybackRecordRole.Checkpoint, label, out var replayRecord))
        {
            recordRunners[replayRecord.Index] = runner;
            stopIdsByRecord[replayRecord.Index] = stopId;
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
        stopIdsByRecord[record.Index] = stopId;
        return record.Index;
    }

    internal int AddCall(IPlaybackRunner parent, IPlaybackRunner child, string label)
    {
        if (TryConsumeReplayRecord(PlaybackRecordRole.Call, label, out var replayRecord))
        {
            recordRunners[replayRecord.Index] = child;
            stopIdsByRecord[replayRecord.Index] = NoStopId;
            child.SetCallRecordIndex(replayRecord.Index);
            return replayRecord.Index;
        }

        TruncateIfRewriting();
        var record = AddRecord(
            PlaybackRecordRole.Call,
            label,
            parent.Depth,
            parent.CallRecordIndex ?? -1
        );
        recordRunners[record.Index] = child;
        stopIdsByRecord[record.Index] = NoStopId;
        child.SetCallRecordIndex(record.Index);
        return record.Index;
    }

    internal int AddCallEnd(IPlaybackRunner runner, int stopId, int callRecordIndex)
    {
        const string label = "Out";

        if (TryConsumeReplayRecord(PlaybackRecordRole.CallEnd, label, out var replayRecord))
        {
            recordRunners[replayRecord.Index] = runner;
            stopIdsByRecord[replayRecord.Index] = stopId;
            return replayRecord.Index;
        }

        TruncateIfRewriting();
        var record = AddRecord(PlaybackRecordRole.CallEnd, label, runner.Depth, callRecordIndex);
        recordRunners[record.Index] = runner;
        stopIdsByRecord[record.Index] = stopId;
        return record.Index;
    }

    internal void OnRootCompleted()
    {
        const string label = "Completed";

        if (TryConsumeReplayRecord(PlaybackRecordRole.Completed, label, out _))
        {
            IsCompleted = true;
            return;
        }

        TruncateIfRewriting();
        AddRecord(PlaybackRecordRole.Completed, label, 0, -1);
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

        var runner = PrepareRunner(startIndex);

        MoveRunnerFrom(startIndex, runner);

        if (mode == PlaybackMode.Replaying)
            throw new InvalidOperationException("Replay did not reach the expected checkpoint.");
    }

    private bool RewriteNext()
    {
        TruncateIfRewriting();

        var runner = PrepareRunner(cursor);

        MoveRunnerFrom(cursor, runner);

        var next = FindNextStop(cursor);
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

        var runner = PrepareRunner(cursor);

        MoveRunnerFrom(cursor, runner);
        return true;
    }

    private IPlaybackRunner PrepareRunner(int recordIndex)
    {
        if (recordIndex < 0)
        {
            var runner = rootRunner!;
            rootRunner!.RestoreInitial();
            return runner;
        }

        var record = records[recordIndex];
        if (record.Role == PlaybackRecordRole.Call)
        {
            var runner = GetRunner(record);
            runner.RestoreInitial();
            return runner;
        }

        if (record.Role == PlaybackRecordRole.CallEnd)
        {
            var runner = GetRunner(record);
            runner.ResetForReplay();
            return runner;
        }

        return RestoreStop(record);
    }

    private IPlaybackRunner RestoreStop(PlaybackRecord record)
    {
        var runner = GetRunner(record);
        var stopId = stopIdsByRecord[record.Index];
        if (stopId == NoStopId)
            throw new InvalidOperationException("Record stop was not found.");

        runner.RestoreStop(stopId, record.Index);
        return runner;
    }

    private void MoveRunnerFrom(int recordIndex, IPlaybackRunner runner)
    {
        if (recordIndex >= 0 && records[recordIndex].Role == PlaybackRecordRole.CallEnd)
        {
            var stopId = stopIdsByRecord[recordIndex];
            if (stopId == NoStopId)
                throw new InvalidOperationException("Record stop was not found.");

            runner.ResumeStop(stopId);
            return;
        }

        runner.MoveNext();
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
            Array.Fill(stopIdsByRecord, NoStopId, index, recordCount - index);
            recordCount = index;
        }
        rewriteFrom = -1;
        mode = PlaybackMode.Normal;
    }

    private int FindNextStop(int afterIndex)
    {
        for (var i = afterIndex + 1; i < recordCount; i++)
            if (IsStop(records[i]))
                return i;

        return -1;
    }

    private int FindPreviousStop(int beforeIndex)
    {
        for (var i = beforeIndex - 1; i >= 0; i--)
            if (IsStop(records[i]))
                return i;

        return -1;
    }

    private bool IsStop(PlaybackRecord record)
    {
        return record.Role
            is PlaybackRecordRole.Checkpoint
                or PlaybackRecordRole.Call
                or PlaybackRecordRole.CallEnd
                or PlaybackRecordRole.Completed;
    }

    private void EnsureRecordCapacity()
    {
        if (recordCount < records.Length)
            return;

        var oldCapacity = records.Length;
        var capacity = records.Length == 0 ? 8 : records.Length * 2;
        Array.Resize(ref records, capacity);
        Array.Resize(ref recordRunners, capacity);
        Array.Resize(ref stopIdsByRecord, capacity);
        Array.Fill(stopIdsByRecord, NoStopId, oldCapacity, capacity - oldCapacity);
    }

    private enum PlaybackMode
    {
        Normal,
        Rewriting,
        Replaying,
    }
}
