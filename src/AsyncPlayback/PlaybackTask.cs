using System.Runtime.CompilerServices;

namespace AsyncPlayback;

[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder))]
public readonly partial struct PlaybackTask
{
    internal readonly PlaybackPromise? Promise;
    private readonly bool completed;
    private readonly bool replaySuspended;
    private readonly int replayOwnerRecordIndex;

    internal PlaybackTask(PlaybackPromise promise)
    {
        Promise = promise;
        completed = false;
        replaySuspended = false;
        replayOwnerRecordIndex = -1;
    }

    private PlaybackTask(bool completed)
    {
        Promise = null;
        this.completed = completed;
        replaySuspended = false;
        replayOwnerRecordIndex = -1;
    }

    internal static PlaybackTask Completed { get; } = new(true);

    internal static PlaybackTask SuspendReplayAt(int ownerRecordIndex)
    {
        return new(ownerRecordIndex);
    }

    private PlaybackTask(int ownerRecordIndex)
    {
        Promise = null;
        completed = false;
        replaySuspended = true;
        replayOwnerRecordIndex = ownerRecordIndex;
    }

    internal static PlaybackTask FromException(Playback playback, Exception exception)
    {
        return new(PlaybackPromise.FromException(playback, exception));
    }

    public Awaiter GetAwaiter()
    {
        if (Promise == null && !completed && !replaySuspended)
            throw new InvalidOperationException("Default PlaybackTask cannot be awaited.");

        return new(Promise, completed, replaySuspended, replayOwnerRecordIndex);
    }

    public readonly struct Awaiter : ICriticalNotifyCompletion, IPlaybackAwaiter
    {
        private readonly PlaybackPromise? promise;
        private readonly bool completed;
        private readonly bool replaySuspended;
        private readonly int replayOwnerRecordIndex;

        internal Awaiter(
            PlaybackPromise? promise,
            bool completed,
            bool replaySuspended,
            int replayOwnerRecordIndex
        )
        {
            this.promise = promise;
            this.completed = completed;
            this.replaySuspended = replaySuspended;
            this.replayOwnerRecordIndex = replayOwnerRecordIndex;
        }

        public bool IsCompleted => !replaySuspended && (completed || promise!.IsCompleted);

        PlaybackPromiseBase? IPlaybackAwaiter.Promise => promise;
        bool IPlaybackAwaiter.IsReplaySuspension => replaySuspended;
        int IPlaybackAwaiter.ReplayOwnerRecordIndex => replayOwnerRecordIndex;

        public void GetResult()
        {
            if (replaySuspended)
                throw new InvalidOperationException(
                    "Replay suspension cannot be completed directly."
                );

            if (!completed)
                promise!.GetResult();
        }

        public void OnCompleted(Action continuation)
        {
            if (replaySuspended)
                throw new NotSupportedException(
                    "Replay suspension awaiter is handled directly by PlaybackTaskMethodBuilder."
                );

            promise!.AddRawContinuation(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            if (replaySuspended)
                throw new NotSupportedException(
                    "Replay suspension awaiter is handled directly by PlaybackTaskMethodBuilder."
                );

            promise!.AddRawContinuation(continuation);
        }
    }
}

[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder<>))]
public readonly struct PlaybackTask<T>
{
    internal readonly PlaybackPromise<T>? Promise;
    private readonly T? result;
    private readonly bool completed;
    private readonly bool replaySuspended;
    private readonly int replayOwnerRecordIndex;

    internal PlaybackTask(PlaybackPromise<T> promise)
    {
        Promise = promise;
        result = default;
        completed = false;
        replaySuspended = false;
        replayOwnerRecordIndex = -1;
    }

    private PlaybackTask(T result)
    {
        Promise = null;
        this.result = result;
        completed = true;
        replaySuspended = false;
        replayOwnerRecordIndex = -1;
    }

    internal static PlaybackTask<T> FromResult(T result)
    {
        return new(result);
    }

    internal static PlaybackTask<T> SuspendReplayAt(int ownerRecordIndex)
    {
        return new(ownerRecordIndex);
    }

    private PlaybackTask(int ownerRecordIndex)
    {
        Promise = null;
        result = default;
        completed = false;
        replaySuspended = true;
        replayOwnerRecordIndex = ownerRecordIndex;
    }

    public Awaiter GetAwaiter()
    {
        if (Promise == null && !completed && !replaySuspended)
            throw new InvalidOperationException("Default PlaybackTask<T> cannot be awaited.");

        return new(Promise, result, completed, replaySuspended, replayOwnerRecordIndex);
    }

    public readonly struct Awaiter : ICriticalNotifyCompletion, IPlaybackAwaiter
    {
        private readonly PlaybackPromise<T>? promise;
        private readonly T? result;
        private readonly bool completed;
        private readonly bool replaySuspended;
        private readonly int replayOwnerRecordIndex;

        internal Awaiter(
            PlaybackPromise<T>? promise,
            T? result,
            bool completed,
            bool replaySuspended,
            int replayOwnerRecordIndex
        )
        {
            this.promise = promise;
            this.result = result;
            this.completed = completed;
            this.replaySuspended = replaySuspended;
            this.replayOwnerRecordIndex = replayOwnerRecordIndex;
        }

        public bool IsCompleted => !replaySuspended && (completed || promise!.IsCompleted);

        PlaybackPromiseBase? IPlaybackAwaiter.Promise => promise;
        bool IPlaybackAwaiter.IsReplaySuspension => replaySuspended;
        int IPlaybackAwaiter.ReplayOwnerRecordIndex => replayOwnerRecordIndex;

        public T GetResult()
        {
            if (replaySuspended)
                throw new InvalidOperationException(
                    "Replay suspension cannot be completed directly."
                );

            return completed ? result! : promise!.GetResult();
        }

        public void OnCompleted(Action continuation)
        {
            if (replaySuspended)
                throw new NotSupportedException(
                    "Replay suspension awaiter is handled directly by PlaybackTaskMethodBuilder."
                );

            promise!.AddRawContinuation(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            if (replaySuspended)
                throw new NotSupportedException(
                    "Replay suspension awaiter is handled directly by PlaybackTaskMethodBuilder."
                );

            promise!.AddRawContinuation(continuation);
        }
    }
}

public interface IPlaybackAwaiter
{
    PlaybackPromiseBase? Promise => null;
    Playback? playback => null;
    string? DebugLabel => null;
    bool IsReplaySuspension => false;
    int ReplayOwnerRecordIndex => -1;
}
