using System.Runtime.CompilerServices;

namespace MinimumPlayback;

public struct PlaybackTaskMethodBuilder
{
    private PlaybackTaskMethodBuilderCore core;

    public static PlaybackTaskMethodBuilder Create()
    {
        var playback =
            PlaybackRuntime.CurrentPlayback
            ?? throw new InvalidOperationException(
                "PlaybackTask must be created inside a Playback."
            );
        return new() { core = new(playback, new()) };
    }

    public PlaybackTask Task => new(core.Promise);

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        core.Start(ref stateMachine);
    }

    public void SetResult() => core.Complete();

    public void SetException(Exception exception) => core.Fault(exception);

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine
    )
        where TAwaiter : INotifyCompletion, IPlaybackAwaiter
        where TStateMachine : IAsyncStateMachine
    {
        core.CaptureAwait(ref awaiter, ref stateMachine);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine
    )
        where TAwaiter : ICriticalNotifyCompletion, IPlaybackAwaiter
        where TStateMachine : IAsyncStateMachine
    {
        core.CaptureAwait(ref awaiter, ref stateMachine);
    }
}

public struct PlaybackTaskMethodBuilder<T>
{
    private PlaybackTaskMethodBuilderCore core;
    private PlaybackPromise<T>? promise;

    public static PlaybackTaskMethodBuilder<T> Create()
    {
        var playback =
            PlaybackRuntime.CurrentPlayback
            ?? throw new InvalidOperationException(
                "PlaybackTask<T> must be created inside a Playback."
            );
        var promise = new PlaybackPromise<T>();
        return new() { core = new(playback, promise), promise = promise };
    }

    public PlaybackTask<T> Task => new(promise!);

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        core.Start(ref stateMachine);
    }

    public void SetResult(T result) => core.Complete(promise!, result);

    public void SetException(Exception exception) => core.Fault(exception);

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine
    )
        where TAwaiter : INotifyCompletion, IPlaybackAwaiter
        where TStateMachine : IAsyncStateMachine
    {
        core.CaptureAwait(ref awaiter, ref stateMachine);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine
    )
        where TAwaiter : ICriticalNotifyCompletion, IPlaybackAwaiter
        where TStateMachine : IAsyncStateMachine
    {
        core.CaptureAwait(ref awaiter, ref stateMachine);
    }
}

internal sealed class PlaybackTaskMethodBuilderCore
{
    private readonly Playback playback;
    public PlaybackPromise Promise { get; }
    private IPlaybackRunner? runner;

    public PlaybackTaskMethodBuilderCore(Playback playback, PlaybackPromise promise)
    {
        this.playback = playback;
        Promise = promise;
    }

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        var parent = PlaybackRuntime.CurrentRunner;
        var typedRunner = new PlaybackRunner<TStateMachine>(playback, Promise, parent);
        runner = typedRunner;
        Promise.AttachRunner(runner);
        if (parent == null)
            playback.AttachRootRunner(runner);
        typedRunner.SetInitial(ref stateMachine);
        runner.MoveNext();
    }

    public void CaptureAwait<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine
    )
        where TAwaiter : IPlaybackAwaiter
        where TStateMachine : IAsyncStateMachine
    {
        var typedRunner =
            runner as PlaybackRunner<TStateMachine>
            ?? throw new InvalidOperationException("State machine runner is missing.");

        if (awaiter.IsReplaySuspension)
        {
            typedRunner.CaptureReplaySuspension(ref stateMachine, awaiter.ReplayOwnerRecordIndex);
            return;
        }

        if (awaiter.CheckpointLabel is { } label)
        {
            typedRunner.CaptureCheckpoint(ref stateMachine, label);
            return;
        }

        var promise =
            awaiter.Promise ?? throw new InvalidOperationException("Awaiter is missing a promise.");
        promise.AddContinuation(typedRunner, typedRunner.CaptureAwait(ref stateMachine));
    }

    public void Complete()
    {
        runner?.MarkCompleted();
        Promise.TrySetResult();
    }

    public void Complete<T>(PlaybackPromise<T> promise, T result)
    {
        runner?.MarkCompleted();
        promise.TrySetResult(result);
    }

    public void Fault(Exception exception)
    {
        runner?.MarkCompleted();
        Promise.TrySetException(exception);
    }
}
