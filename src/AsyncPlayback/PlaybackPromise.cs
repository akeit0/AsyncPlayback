namespace AsyncPlayback;

public abstract class PlaybackPromiseBase
{
    private readonly List<IContinuation> continuations = [];

    private readonly Playback playback;
    private Exception? exception;

    private PromiseStatus status;

    protected PlaybackPromiseBase(Playback playback, PlaybackPromiseKind kind)
    {
        this.playback = playback;
        Kind = kind;
    }

    public PlaybackPromiseKind Kind { get; }

    public TimeSpan StartTime { get; set; }
    public TimeSpan Duration { get; set; }
    public TimeSpan DueTime { get; set; }
    internal TimelineRecord? OwnerRecord { get; set; }

    public bool IsCompleted => status != PromiseStatus.Pending;
    internal IPlaybackRunner? Runner { get; private set; }

    internal abstract bool TrySetObjectResult(object? result);

    internal void AttachRunner(IPlaybackRunner runner)
    {
        Runner = runner;
    }

    public virtual void ResetForReplay()
    {
        status = PromiseStatus.Pending;
        exception = null;
        continuations.Clear();
    }

    public bool TrySetException(Exception exception)
    {
        if (status != PromiseStatus.Pending)
            return false;

        this.exception = exception ?? throw new ArgumentNullException(nameof(exception));
        status = PromiseStatus.Faulted;
        FlushContinuations();
        return true;
    }

    protected bool TryComplete()
    {
        if (status != PromiseStatus.Pending)
            return false;

        status = PromiseStatus.Succeeded;
        FlushContinuations();
        return true;
    }

    protected void ThrowIfNotSucceeded()
    {
        if (status == PromiseStatus.Pending)
            throw new InvalidOperationException("Promise is not completed.");

        if (status == PromiseStatus.Faulted)
            throw exception!;
    }

    internal void AddRunnerContinuation(
        IPlaybackRunner runner,
        int checkpointId,
        long expectedEpoch,
        TimelineRecord? resumeScope
    )
    {
        var continuation = new RunnerContinuation(runner, checkpointId, expectedEpoch, resumeScope);

        if (status == PromiseStatus.Pending)
        {
            continuations.Add(continuation);
            return;
        }

        playback.Post(continuation.Invoke);
    }

    public void AddRawContinuation(Action action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        var continuation = new RawContinuation(action);

        if (status == PromiseStatus.Pending)
        {
            continuations.Add(continuation);
            return;
        }

        playback.Post(continuation.Invoke);
    }

    private void FlushContinuations()
    {
        if (this.continuations.Count == 0)
            return;

        var continuations = this.continuations.ToArray();
        this.continuations.Clear();

        foreach (var continuation in continuations)
            playback.Post(continuation.Invoke);
    }

    private enum PromiseStatus
    {
        Pending,
        Succeeded,
        Faulted,
    }

    private interface IContinuation
    {
        void Invoke();
    }

    private readonly struct RunnerContinuation : IContinuation
    {
        private readonly IPlaybackRunner runner;
        private readonly int checkpointId;
        private readonly long expectedEpoch;
        private readonly TimelineRecord? resumeScope;

        public RunnerContinuation(
            IPlaybackRunner runner,
            int checkpointId,
            long expectedEpoch,
            TimelineRecord? resumeScope
        )
        {
            this.runner = runner;
            this.checkpointId = checkpointId;
            this.expectedEpoch = expectedEpoch;
            this.resumeScope = resumeScope;
        }

        public void Invoke()
        {
            runner.ResumeFromAwait(checkpointId, expectedEpoch, resumeScope);
        }
    }

    private readonly struct RawContinuation : IContinuation
    {
        private readonly Action action;

        public RawContinuation(Action action)
        {
            this.action = action;
        }

        public void Invoke()
        {
            action();
        }
    }
}

internal sealed class PlaybackPromise : PlaybackPromiseBase
{
    public PlaybackPromise(Playback playback, PlaybackPromiseKind kind)
        : base(playback, kind) { }

    public bool TrySetResult()
    {
        return TryComplete();
    }

    internal override bool TrySetObjectResult(object? result)
    {
        return TrySetResult();
    }

    public void GetResult()
    {
        ThrowIfNotSucceeded();
    }
}

internal sealed class PlaybackPromise<T> : PlaybackPromiseBase
{
    private T? result;

    public PlaybackPromise(Playback playback, PlaybackPromiseKind kind)
        : base(playback, kind) { }

    public override void ResetForReplay()
    {
        base.ResetForReplay();
        result = default;
    }

    public bool TrySetResult(T result)
    {
        this.result = result;
        return TryComplete();
    }

    internal override bool TrySetObjectResult(object? result)
    {
        return TrySetResult((T)result!);
    }

    public T GetResult()
    {
        ThrowIfNotSucceeded();
        return result!;
    }
}
