namespace MinimumPlayback;

public class PlaybackPromise
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
        if (!TryBeginComplete())
            return;

        Complete();
    }

    internal void TrySetException(Exception value)
    {
        if (!TryBeginComplete())
            return;

        exception = value;
        Complete();
    }

    internal void GetResult()
    {
        if (!completed)
            throw new InvalidOperationException("Playback task is not completed.");
        if (exception != null)
            throw exception;
    }

    protected bool TryBeginComplete()
    {
        if (completed)
            return false;

        completed = true;
        return true;
    }

    protected void Complete()
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

public sealed class PlaybackPromise<T> : PlaybackPromise
{
    private T? result;

    internal void TrySetResult(T value)
    {
        if (!TryBeginComplete())
            return;

        result = value;
        Complete();
    }

    internal new T GetResult()
    {
        base.GetResult();
        return result!;
    }
}
