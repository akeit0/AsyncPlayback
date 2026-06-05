namespace AsyncPlayback;

public readonly struct SeekLoopEnumerable
{
    private readonly Playback playback;
    private readonly int recordIndex;

    internal SeekLoopEnumerable(Playback playback, int recordIndex)
    {
        this.playback = playback;
        this.recordIndex = recordIndex;
    }

    public SeekLoopEnumerator GetAsyncEnumerator()
    {
        return new(playback, recordIndex);
    }
}

public readonly struct SeekLoopEnumerator
{
    private readonly int recordIndex;
    private readonly Playback playback;

    internal SeekLoopEnumerator(Playback playback, int recordIndex)
    {
        this.playback = playback;
        this.recordIndex = recordIndex;
    }

    public SeekLoopProgress Current
    {
        get
        {
            var record = playback.GetSeekLoopRecord(recordIndex);
            var elpased = playback.GetSeekLoopElapsed(recordIndex);
            var progress =
                record.Duration > TimeSpan.Zero
                    ? elpased.TotalSeconds / record.Duration.TotalSeconds
                    : 0.0;
            return new SeekLoopProgress(progress, record.Duration);
        }
    }

    public PlaybackTask<bool> MoveNextAsync()
    {
        return playback.ArmSeekLoopMoveNext(recordIndex);
    }
}

public readonly struct SeekLoopProgress(double progress, TimeSpan duration)
{
    public double Progress { get; } = progress;
    public TimeSpan Duration { get; } = duration;
    public TimeSpan Elapsed => TimeSpan.FromSeconds(Progress * Duration.TotalSeconds);

    public override string ToString()
    {
        return $"Progress: {Progress:P}, Duration: {Duration}";
    }
}
