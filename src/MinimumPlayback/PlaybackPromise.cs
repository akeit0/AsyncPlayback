namespace MinimumPlayback;

public class PlaybackPromise
{
    private Action? continuation;
    private Exception? exception;
    private bool completed;

    internal bool IsCompleted => completed;
    internal IPlaybackRunner? Runner { get; private set; }

    internal void AttachRunner(IPlaybackRunner runner) => Runner = runner;

    internal void AddContinuation(Action continuation)
    {
        if (completed)
        {
            continuation();
            return;
        }

        if (this.continuation != null)
            throw new InvalidOperationException("PlaybackTask supports only one awaiter.");

        this.continuation = continuation;
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
        var callback = continuation;
        continuation = null;
        callback?.Invoke();
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
