using System.Runtime.CompilerServices;

namespace AsyncPlayback;

[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder))]
public readonly partial struct PlaybackTask
{
    internal readonly PlaybackPromise? Promise;

    internal PlaybackTask(PlaybackPromise promise)
    {
        Promise = promise;
    }

    public Awaiter GetAwaiter()
    {
        if (Promise == null)
            throw new InvalidOperationException("Default PlaybackTask cannot be awaited.");

        return new(Promise);
    }

    public readonly struct Awaiter : ICriticalNotifyCompletion, IPlaybackAwaiter
    {
        private readonly PlaybackPromise promise;

        internal Awaiter(PlaybackPromise promise)
        {
            this.promise = promise;
        }

        public bool IsCompleted => promise.IsCompleted;

        PlaybackPromiseBase IPlaybackAwaiter.Promise => promise;

        public void GetResult()
        {
            promise.GetResult();
        }

        public void OnCompleted(Action continuation)
        {
            promise.AddRawContinuation(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            promise.AddRawContinuation(continuation);
        }
    }
}

[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder<>))]
public readonly struct PlaybackTask<T>
{
    internal readonly PlaybackPromise<T>? Promise;

    internal PlaybackTask(PlaybackPromise<T> promise)
    {
        Promise = promise;
    }

    public Awaiter GetAwaiter()
    {
        if (Promise == null)
            throw new InvalidOperationException("Default PlaybackTask<T> cannot be awaited.");

        return new(Promise);
    }

    public readonly struct Awaiter : ICriticalNotifyCompletion, IPlaybackAwaiter
    {
        private readonly PlaybackPromise<T> promise;

        internal Awaiter(PlaybackPromise<T> promise)
        {
            this.promise = promise;
        }

        public bool IsCompleted => promise.IsCompleted;

        PlaybackPromiseBase IPlaybackAwaiter.Promise => promise;

        public T GetResult()
        {
            return promise.GetResult();
        }

        public void OnCompleted(Action continuation)
        {
            promise.AddRawContinuation(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            promise.AddRawContinuation(continuation);
        }
    }
}

public interface IPlaybackAwaiter
{
    PlaybackPromiseBase? Promise => null;
    Playback? playback => null;
    string? DebugLabel => null;
}
