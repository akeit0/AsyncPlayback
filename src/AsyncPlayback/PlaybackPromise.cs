namespace AsyncPlayback;

public abstract class PlaybackPromiseBase
{
    private static readonly Action<RunnerContinuation> InvokeRunnerContinuation = static state =>
        state.Invoke();

    private static readonly Action<Action> InvokeRawContinuation = static action => action();

    private readonly List<PendingContinuation> continuations = [];

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
    internal int? OwnerRecordIndex { get; set; }

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
        int? resumeScope
    )
    {
        var continuation = PendingContinuation.ForRunner(
            new(runner, checkpointId, expectedEpoch, resumeScope)
        );

        if (status == PromiseStatus.Pending)
        {
            continuations.Add(continuation);
            return;
        }

        continuation.Post(playback);
    }

    public void AddRawContinuation(Action action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        var continuation = PendingContinuation.ForRaw(action);

        if (status == PromiseStatus.Pending)
        {
            continuations.Add(continuation);
            return;
        }

        continuation.Post(playback);
    }

    private void FlushContinuations()
    {
        if (this.continuations.Count == 0)
            return;

        var continuations = this.continuations.ToArray();
        this.continuations.Clear();

        foreach (var continuation in continuations)
            continuation.Post(playback);
    }

    private enum PromiseStatus
    {
        Pending,
        Succeeded,
        Faulted,
    }

    private readonly struct PendingContinuation
    {
        private readonly PendingContinuationKind kind;
        private readonly RunnerContinuation? runner;
        private readonly Action? rawAction;

        private PendingContinuation(
            PendingContinuationKind kind,
            RunnerContinuation? runner,
            Action? rawAction
        )
        {
            this.kind = kind;
            this.runner = runner;
            this.rawAction = rawAction;
        }

        public static PendingContinuation ForRunner(RunnerContinuation runner)
        {
            return new(PendingContinuationKind.Runner, runner, null);
        }

        public static PendingContinuation ForRaw(Action action)
        {
            return new(PendingContinuationKind.Raw, null, action);
        }

        public void Post(Playback playback)
        {
            switch (kind)
            {
                case PendingContinuationKind.Runner:
                    playback.Post(runner!, InvokeRunnerContinuation);
                    break;

                case PendingContinuationKind.Raw:
                    playback.Post(rawAction!, InvokeRawContinuation);
                    break;

                default:
                    throw new InvalidOperationException("Unknown continuation kind.");
            }
        }
    }

    private enum PendingContinuationKind
    {
        Runner,
        Raw,
    }

    private sealed class RunnerContinuation
    {
        private readonly IPlaybackRunner runner;
        private readonly int checkpointId;
        private readonly long expectedEpoch;
        private readonly int? resumeScope;

        public RunnerContinuation(
            IPlaybackRunner runner,
            int checkpointId,
            long expectedEpoch,
            int? resumeScope
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
