namespace MinimumPlayback;

public class PlaybackPromise
{
    private IPlaybackRunner? continuationRunner;
    private int continuationId = -1;
    private Exception? exception;
    private bool completed;

    internal bool IsCompleted => completed;
    internal IPlaybackRunner? Runner { get; private set; }

    internal void AttachRunner(IPlaybackRunner runner) => Runner = runner;

    internal void AddContinuation(IPlaybackRunner runner, int id)
    {
        if (completed)
        {
            runner.CompleteAwait(id, Runner?.CallRecordIndex ?? -1);
            return;
        }

        if (continuationRunner != null)
            throw new InvalidOperationException("PlaybackTask supports only one awaiter.");

        continuationRunner = runner;
        continuationId = id;
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

    internal void ResetForReplay()
    {
        completed = false;
        exception = null;
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
        continuationRunner?.CompleteAwait(continuationId, Runner?.CallRecordIndex ?? -1);
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
