using System.Runtime.CompilerServices;

namespace MinimumPlayback;

[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder))]
public readonly partial struct PlaybackTask
{
    private readonly PlaybackPromise? promise;
    private readonly bool replaySuspended;
    private readonly int replayOwnerRecordIndex;

    internal PlaybackTask(PlaybackPromise promise)
    {
        this.promise = promise;
        replaySuspended = false;
        replayOwnerRecordIndex = -1;
    }

    private PlaybackTask(int ownerRecordIndex)
    {
        promise = null;
        replaySuspended = true;
        replayOwnerRecordIndex = ownerRecordIndex;
    }

    internal static PlaybackTask SuspendReplayAt(int ownerRecordIndex) => new(ownerRecordIndex);

    public Awaiter GetAwaiter()
    {
        if (promise == null && !replaySuspended)
            throw new InvalidOperationException("Default PlaybackTask cannot be awaited.");

        return new(promise, replaySuspended, replayOwnerRecordIndex);
    }

    public readonly struct Awaiter : INotifyCompletion, IPlaybackAwaiter
    {
        private readonly PlaybackPromise? promise;
        private readonly bool replaySuspended;
        private readonly int replayOwnerRecordIndex;

        internal Awaiter(PlaybackPromise? promise, bool replaySuspended, int replayOwnerRecordIndex)
        {
            this.promise = promise;
            this.replaySuspended = replaySuspended;
            this.replayOwnerRecordIndex = replayOwnerRecordIndex;
        }

        public bool IsCompleted => false; //!replaySuspended && promise!.IsCompleted;
        PlaybackPromise? IPlaybackAwaiter.Promise => promise;
        bool IPlaybackAwaiter.IsReplaySuspension => replaySuspended;
        int IPlaybackAwaiter.ReplayOwnerRecordIndex => replayOwnerRecordIndex;

        public void GetResult()
        {
            if (replaySuspended)
                throw new InvalidOperationException("Replay suspension cannot complete directly.");
            promise!.GetResult();
        }

        public void OnCompleted(Action continuation) =>
            throw new InvalidOperationException(
                "PlaybackTask can only be awaited by PlaybackTask."
            );
    }
}

public interface IPlaybackAwaiter
{
    PlaybackPromise? Promise => null;
    Playback? Playback => null;
    string? CheckpointLabel => null;
    bool IsReplaySuspension => false;
    int ReplayOwnerRecordIndex => -1;
}

[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder<>))]
public readonly struct PlaybackTask<T>
{
    private readonly PlaybackPromise<T>? promise;
    private readonly bool replaySuspended;
    private readonly int replayOwnerRecordIndex;

    internal PlaybackTask(PlaybackPromise<T> promise)
    {
        this.promise = promise;
        replaySuspended = false;
        replayOwnerRecordIndex = -1;
    }

    private PlaybackTask(int ownerRecordIndex)
    {
        promise = null;
        replaySuspended = true;
        replayOwnerRecordIndex = ownerRecordIndex;
    }

    internal static PlaybackTask<T> SuspendReplayAt(int ownerRecordIndex) => new(ownerRecordIndex);

    public Awaiter GetAwaiter()
    {
        if (promise == null && !replaySuspended)
            throw new InvalidOperationException("Default PlaybackTask<T> cannot be awaited.");

        return new(promise, replaySuspended, replayOwnerRecordIndex);
    }

    public readonly struct Awaiter : INotifyCompletion, IPlaybackAwaiter
    {
        private readonly PlaybackPromise<T>? promise;
        private readonly bool replaySuspended;
        private readonly int replayOwnerRecordIndex;

        internal Awaiter(
            PlaybackPromise<T>? promise,
            bool replaySuspended,
            int replayOwnerRecordIndex
        )
        {
            this.promise = promise;
            this.replaySuspended = replaySuspended;
            this.replayOwnerRecordIndex = replayOwnerRecordIndex;
        }

        public bool IsCompleted => false; // !replaySuspended && promise!.IsCompleted && !promise.ShouldForceContinuation;
        PlaybackPromise? IPlaybackAwaiter.Promise => promise;
        bool IPlaybackAwaiter.IsReplaySuspension => replaySuspended;
        int IPlaybackAwaiter.ReplayOwnerRecordIndex => replayOwnerRecordIndex;

        public T GetResult()
        {
            if (replaySuspended)
                throw new InvalidOperationException("Replay suspension cannot complete directly.");
            return promise!.GetResult();
        }

        public void OnCompleted(Action continuation) =>
            throw new InvalidOperationException(
                "PlaybackTask can only be awaited by PlaybackTask."
            );
    }
}
