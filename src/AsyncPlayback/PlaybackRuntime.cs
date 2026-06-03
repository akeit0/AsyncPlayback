namespace AsyncPlayback;

internal static class PlaybackRuntime
{
    [ThreadStatic]
    private static Stack<Playback>? playbacks;

    [ThreadStatic]
    private static Stack<IPlaybackRunner>? runners;

    [ThreadStatic]
    private static Stack<TimelineRecord>? recordScopes;

    public static Playback? Currentplayback
    {
        get
        {
            var stack = playbacks;
            return stack == null || stack.Count == 0 ? null : stack.Peek();
        }
    }

    public static IPlaybackRunner? CurrentRunner
    {
        get
        {
            var stack = runners;
            return stack == null || stack.Count == 0 ? null : stack.Peek();
        }
    }

    public static TimelineRecord? CurrentRecordScope
    {
        get
        {
            var stack = recordScopes;
            return stack == null || stack.Count == 0 ? null : stack.Peek();
        }
    }

    public static void Pushplayback(Playback playback)
    {
        (playbacks ??= new()).Push(playback);
    }

    public static void Popplayback(Playback playback)
    {
        var stack = playbacks;

        if (stack == null || stack.Count == 0 || !ReferenceEquals(stack.Peek(), playback))
            throw new InvalidOperationException("playback stack corruption.");

        stack.Pop();
    }

    public static void PushRunner(IPlaybackRunner runner)
    {
        (runners ??= new()).Push(runner);
    }

    public static void PopRunner(IPlaybackRunner runner)
    {
        var stack = runners;

        if (stack == null || stack.Count == 0 || !ReferenceEquals(stack.Peek(), runner))
            throw new InvalidOperationException("Runner stack corruption.");

        stack.Pop();
    }

    public static void PushRecordScope(TimelineRecord record)
    {
        (recordScopes ??= new()).Push(record);
    }

    public static void PopRecordScope(TimelineRecord record)
    {
        var stack = recordScopes;

        if (stack == null || stack.Count == 0 || !ReferenceEquals(stack.Peek(), record))
            throw new InvalidOperationException("Record scope stack corruption.");

        stack.Pop();
    }
}
