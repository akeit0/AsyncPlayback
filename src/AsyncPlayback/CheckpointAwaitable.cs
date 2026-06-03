using System.Runtime.CompilerServices;

namespace AsyncPlayback;

public readonly struct CheckpointAwaitable
{
    private readonly Playback playback;
    private readonly string debugLabel;

    internal CheckpointAwaitable(Playback playback, string debugLabel)
    {
        this.playback = playback;
        this.debugLabel = debugLabel;
    }

    public CheckpointAwaiter GetAwaiter()
    {
        return new(playback, debugLabel);
    }
}

public readonly struct CheckpointAwaiter : ICriticalNotifyCompletion, IPlaybackAwaiter
{
    private readonly Playback playback;

    internal CheckpointAwaiter(Playback playback, string debugLabel)
    {
        this.playback = playback;
        DebugLabel = debugLabel;
    }

    internal string? DebugLabel { get; }

    public bool IsCompleted => false;

    Playback IPlaybackAwaiter.playback => playback;
    string? IPlaybackAwaiter.DebugLabel => DebugLabel;

    public void GetResult() { }

    public void OnCompleted(Action continuation)
    {
        throw new NotSupportedException(
            "Checkpoint awaiter is handled directly by PlaybackTaskMethodBuilder."
        );
    }

    public void UnsafeOnCompleted(Action continuation)
    {
        throw new NotSupportedException(
            "Checkpoint awaiter is handled directly by PlaybackTaskMethodBuilder."
        );
    }
}
