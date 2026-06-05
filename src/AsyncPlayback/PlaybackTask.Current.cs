namespace AsyncPlayback;

public partial struct PlaybackTask
{
    public static PlaybackDirection CurrentDirection => GetCurrentPlayback().CurrentDirection;
    public static bool IsForward => CurrentDirection == PlaybackDirection.Forward;
    public static bool IsBackward => CurrentDirection == PlaybackDirection.Backward;

    public static CheckpointAwaitable Checkpoint(string debugLabel = "Checkpoint") =>
        new(GetCurrentPlayback(), debugLabel);

    public static PlaybackTask Delay(TimeSpan delay, string debugLabel = "Delay") =>
        GetCurrentPlayback().Delay(delay, debugLabel);

    public static PlaybackTask Effect(
        Func<CancellationToken, ValueTask> effect,
        string debugLabel = "Effect"
    ) => GetCurrentPlayback().Effect(effect, debugLabel);

    public static PlaybackTask<T> Effect<T>(
        Func<CancellationToken, ValueTask<T>> effect,
        string debugLabel = "Effect"
    ) => GetCurrentPlayback().Effect(effect, debugLabel);

    public static SeekLoopEnumerable ForEachOnSeek(
        TimeSpan duration,
        string debugLabel = "ForEachOnSeek"
    ) => GetCurrentPlayback().ForEachOnSeek(duration, debugLabel);

    public static T SelectByDirection<T>(T backwardStore, T forward)
        where T : notnull
    {
        return GetCurrentPlayback().SelectByDirection(backwardStore, forward);
    }

    public static bool TryGet<T>(out T state)
        where T : notnull
    {
        return GetCurrentPlayback().TryGet(out state);
    }

    public static void Store<T>(T state)
        where T : notnull
    {
        GetCurrentPlayback().Store(state);
    }

    public static void ClearStore()
    {
        GetCurrentPlayback().ClearStore();
    }

    public static Playback GetCurrentPlayback()
    {
        var playback = PlaybackRuntime.CurrentPlayback;
        if (playback == null)
            throw new InvalidOperationException("No active playback.");
        return playback;
    }
}
