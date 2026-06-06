namespace MinimumPlayback;

internal static class PlaybackRuntime
{
    [ThreadStatic]
    private static Playback? currentPlayback;

    [ThreadStatic]
    private static IPlaybackRunner? currentRunner;

    public static Playback? CurrentPlayback => currentPlayback;
    public static IPlaybackRunner? CurrentRunner => currentRunner;

    public static Scope Push(Playback playback, IPlaybackRunner? runner)
    {
        var previousPlayback = currentPlayback;
        var previousRunner = currentRunner;
        currentPlayback = playback;
        currentRunner = runner;
        return new(previousPlayback, previousRunner);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly Playback? playback;
        private readonly IPlaybackRunner? runner;

        public Scope(Playback? playback, IPlaybackRunner? runner)
        {
            this.playback = playback;
            this.runner = runner;
        }

        public void Dispose()
        {
            currentPlayback = playback;
            currentRunner = runner;
        }
    }
}
