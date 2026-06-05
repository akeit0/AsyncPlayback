namespace AsyncPlayback;

public readonly struct SeekLoopEnumerable
{
    private readonly Playback playback;
    private readonly SeekLoopRecord record;

    internal SeekLoopEnumerable(Playback playback, SeekLoopRecord record)
    {
        this.playback = playback;
        this.record = record;
    }

    public SeekLoopEnumerator GetAsyncEnumerator()
    {
        return new(playback, record);
    }
}

public readonly struct SeekLoopEnumerator
{
    private readonly SeekLoopRecord record;
    private readonly Playback playback;

    internal SeekLoopEnumerator(Playback playback, SeekLoopRecord record)
    {
        this.playback = playback;
        this.record = record;
    }

    public SeekLoopProgress Current
    {
        get
        {
            var elpased = playback.GetSeekLoopElapsed(record);
            var progress =
                record.Duration > TimeSpan.Zero
                    ? elpased.TotalSeconds / record.Duration.TotalSeconds
                    : 0.0;
            return new SeekLoopProgress(progress, record.Duration);
        }
    }

    public PlaybackTask<bool> MoveNextAsync()
    {
        return playback.ArmSeekLoopMoveNext(record);
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
