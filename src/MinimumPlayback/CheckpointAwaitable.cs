using System.Runtime.CompilerServices;

namespace MinimumPlayback;

public readonly struct CheckpointAwaitable
{
    private readonly Playback playback;
    private readonly string label;

    internal CheckpointAwaitable(Playback playback, string label)
    {
        this.playback = playback;
        this.label = label;
    }

    public Awaiter GetAwaiter() => new(playback, label);

    public readonly struct Awaiter : INotifyCompletion, IPlaybackAwaiter
    {
        private readonly Playback playback;
        private readonly string label;

        internal Awaiter(Playback playback, string label)
        {
            this.playback = playback;
            this.label = label;
        }

        public bool IsCompleted => false;
        Playback IPlaybackAwaiter.Playback => playback;
        string? IPlaybackAwaiter.CheckpointLabel => label;

        public void GetResult() { }

        public void OnCompleted(Action continuation) { }
    }
}
