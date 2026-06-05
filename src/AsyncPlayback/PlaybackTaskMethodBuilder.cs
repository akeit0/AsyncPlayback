using System.Runtime.CompilerServices;

namespace AsyncPlayback;

public sealed class PlaybackTaskMethodBuilder
{
    private readonly PlaybackTaskMethodBuilderCore core;
    private readonly PlaybackPromise promise;

    private PlaybackTaskMethodBuilder(Playback playback)
    {
        promise = new(playback, PlaybackPromiseKind.AsyncMethod);
        core = new(playback, promise);
    }

    public PlaybackTask Task => new(promise);

    public static PlaybackTaskMethodBuilder Create()
    {
        return new(PlaybackTaskMethodBuilderCore.RequireCurrentPlayback());
    }

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        core.Start(ref stateMachine);
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void SetResult()
    {
        core.CompleteSuccessfully();
        promise.TrySetResult();
    }

    public void SetException(Exception exception)
    {
        core.CompleteFaulted(exception);
    }

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

public sealed class PlaybackTaskMethodBuilder<T>
{
    private readonly PlaybackTaskMethodBuilderCore core;
    private readonly PlaybackPromise<T> promise;

    private PlaybackTaskMethodBuilder(Playback playback)
    {
        promise = new(playback, PlaybackPromiseKind.AsyncMethod);
        core = new(playback, promise);
    }

    public PlaybackTask<T> Task => new(promise);

    public static PlaybackTaskMethodBuilder<T> Create()
    {
        return new(PlaybackTaskMethodBuilderCore.RequireCurrentPlayback());
    }

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        core.Start(ref stateMachine);
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void SetResult(T result)
    {
        core.CompleteSuccessfully();
        promise.TrySetResult(result);
    }

    public void SetException(Exception exception)
    {
        core.CompleteFaulted(exception);
    }

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

internal static class FormatStateMachineNameCache<T>
{
    public static readonly string Name = FormatGeneratedStateMachineName(typeof(T).Name);

    private static string FormatGeneratedStateMachineName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var localFunctionMarker = "g__";
        var localFunctionStart = name.IndexOf(localFunctionMarker, StringComparison.Ordinal);
        if (localFunctionStart >= 0)
        {
            var start = localFunctionStart + localFunctionMarker.Length;
            var end = name.IndexOf('|', start);
            if (end < 0)
                end = name.IndexOf('>', start);

            if (end > start)
                return name[start..end];
        }

        if (name[0] == '<')
        {
            var end = name.IndexOf('>');
            if (end > 1 && name.IndexOf("d__", end, StringComparison.Ordinal) >= 0)
                return name[1..end];
        }

        return name;
    }
}

internal sealed class PlaybackTaskMethodBuilderCore
{
    private readonly IPlaybackRunner? parentRunner;
    private readonly PlaybackPromiseBase promise;
    private readonly Playback playback;
    private IPlaybackRunner? runner;

    public PlaybackTaskMethodBuilderCore(Playback playback, PlaybackPromiseBase promise)
    {
        this.playback = playback;
        this.promise = promise;
        parentRunner = PlaybackRuntime.CurrentRunner;
    }

    public static Playback RequireCurrentPlayback()
    {
        return PlaybackRuntime.CurrentPlayback
            ?? throw new InvalidOperationException(
                "PlaybackTask async methods must be started inside Playback.Start(...)."
            );
    }

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        var runner = new PlaybackRunner<TStateMachine>(
            playback,
            promise,
            parentRunner,
            ref stateMachine
        );

        this.runner = runner;
        promise.AttachRunner(runner);
        var stateMachineName = FormatStateMachineNameCache<TStateMachine>.Name;

        if (parentRunner == null)
        {
            playback.AttachRootRunner(runner);
            playback.RegisterRunnerEntry(runner, null, $"entry {stateMachineName}");
        }
        else
        {
            var call = playback.GetOrCreateCallRecord(parentRunner, runner, stateMachineName);

            runner.BindCurrentCallRecord(call);

            playback.RegisterRunnerEntry(runner, call, $"entry {stateMachineName}");
        }

        playback.Post(runner.MoveNext);
    }

    public void CompleteSuccessfully()
    {
        if (runner == null)
            return;

        runner.MarkCompleted();
        playback.OnRunnerCompleted(runner);
    }

    public void CompleteFaulted(Exception exception)
    {
        if (runner != null)
        {
            runner.MarkCompleted();
            playback.OnRunnerFaulted(runner, exception);
        }

        promise.TrySetException(exception);
    }

    public void CaptureAwait<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine
    )
        where TAwaiter : IPlaybackAwaiter
        where TStateMachine : IAsyncStateMachine
    {
        var awaitedPromise = awaiter.Promise;
        if (awaitedPromise == null)
        {
            var debugLabel = awaiter.DebugLabel ?? "Checkpoint";

            CaptureCheckpointAwaiter(awaiter.playback!, debugLabel, ref stateMachine);
            return;
        }

        var typedRunner = runner as PlaybackRunner<TStateMachine>;
        if (typedRunner == null)
            throw new InvalidOperationException(
                "State machine runner is missing or has unexpected type."
            );

        var ownerRecord = awaitedPromise.OwnerRecord;

        var resumeScope = PlaybackRuntime.CurrentRecordScope ?? typedRunner.CurrentCallRecord;

        var checkpointId = typedRunner.CaptureCheckpoint(
            ref stateMachine,
            awaitedPromise.Kind,
            awaitedPromise,
            ownerRecord,
            resumeScope
        );

        if (awaitedPromise.Kind == PlaybackPromiseKind.AsyncMethod && awaitedPromise.Runner != null)
            awaitedPromise.Runner.SetParentAwaitCheckpointId(checkpointId);

        awaitedPromise.AddRunnerContinuation(
            typedRunner,
            checkpointId,
            typedRunner.Epoch,
            resumeScope
        );
    }

    private void CaptureCheckpointAwaiter<TStateMachine>(
        Playback awaiterplayback,
        string debugLabel,
        ref TStateMachine stateMachine
    )
        where TStateMachine : IAsyncStateMachine
    {
        if (!ReferenceEquals(awaiterplayback, playback))
            throw new InvalidOperationException("Checkpoint belongs to a different playback.");

        var typedRunner = runner as PlaybackRunner<TStateMachine>;
        if (typedRunner == null)
            throw new InvalidOperationException(
                "State machine runner is missing or has unexpected type."
            );

        var record = playback.GetOrCreateCheckpointRecord(debugLabel);

        var resumeScope = PlaybackRuntime.CurrentRecordScope;

        var checkpointId = typedRunner.CaptureCheckpoint(
            ref stateMachine,
            PlaybackPromiseKind.Checkpoint,
            null,
            record,
            resumeScope
        );

        var expectedEpoch = typedRunner.Epoch;

        if (!playback.SuppressCheckpointAutoContinuation)
            playback.Post(() =>
            {
                typedRunner.ResumeFromAwait(checkpointId, expectedEpoch, resumeScope);
            });
    }
}
