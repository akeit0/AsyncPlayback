namespace MinimumPlayback;

public sealed class PlaybackPromise
{
    private Action[] continuations = [];
    private int continuationCount;
    private Exception? exception;
    private bool completed;

    internal bool IsCompleted => completed;
    internal IPlaybackRunner? Runner { get; private set; }

    internal void AttachRunner(IPlaybackRunner runner) => Runner = runner;

    internal void AddContinuation(Action continuation)
    {
        if (completed)
            continuation();
        else
        {
            EnsureContinuationCapacity();
            continuations[continuationCount++] = continuation;
        }
    }

    internal void TrySetResult()
    {
        if (completed)
            return;

        completed = true;
        Flush();
    }

    internal void TrySetException(Exception value)
    {
        if (completed)
            return;

        exception = value;
        completed = true;
        Flush();
    }

    internal void GetResult()
    {
        if (!completed)
            throw new InvalidOperationException("Playback task is not completed.");
        if (exception != null)
            throw exception;
    }

    private void Flush()
    {
        for (var i = 0; i < continuationCount; i++)
            continuations[i]();
        Array.Clear(continuations, 0, continuationCount);
        continuationCount = 0;
    }

    private void EnsureContinuationCapacity()
    {
        if (continuationCount < continuations.Length)
            return;

        Array.Resize(ref continuations, continuations.Length == 0 ? 4 : continuations.Length * 2);
    }
}
