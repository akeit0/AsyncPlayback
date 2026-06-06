namespace AsyncPlayback;

public sealed partial class Playback
{
    internal interface IPlaybackAwaitBehavior
    {
        void OnCaptured(
            Playback playback,
            IPlaybackRunner runner,
            int checkpointId,
            PlaybackPromiseBase? promise,
            int? resumeScope
        )
        {
            promise!.AddRunnerContinuation(runner, checkpointId, runner.Epoch, resumeScope);
        }
        void Arm(Playback playback, TimelineCheckpoint checkpoint)
        {
            var promise =
                checkpoint.AwaitedPromise
                ?? throw new InvalidOperationException("Checkpoint has no promise.");
            promise.ResetForReplay();
            promise.AddRunnerContinuation(
                checkpoint.Runner,
                checkpoint.CheckpointId,
                checkpoint.Runner.Epoch,
                checkpoint.ResumeScope
            );
            ArmPromise(playback, checkpoint, promise);
        }
        void ArmPromise(
            Playback playback,
            TimelineCheckpoint checkpoint,
            PlaybackPromiseBase promise
        ) { }
    }

    internal sealed class CheckpointAwaitBehavior : IPlaybackAwaitBehavior
    {
        public static CheckpointAwaitBehavior Instance { get; } = new();

        public void OnCaptured(
            Playback playback,
            IPlaybackRunner runner,
            int checkpointId,
            PlaybackPromiseBase? promise,
            int? resumeScope
        )
        {
            if (!playback.SuppressCheckpointAutoContinuation)
                playback.Post(
                    new PostedCheckpointResume(runner, checkpointId, runner.Epoch, resumeScope),
                    ResumeCheckpoint
                );
        }

        public void Arm(Playback playback, TimelineCheckpoint checkpoint)
        {
            playback.Post(
                new PostedCheckpointResume(
                    checkpoint.Runner,
                    checkpoint.CheckpointId,
                    checkpoint.Runner.Epoch,
                    checkpoint.ResumeScope
                ),
                ResumeCheckpoint
            );
        }
    }

    internal sealed class AsyncMethodAwaitBehavior : IPlaybackAwaitBehavior
    {
        public static AsyncMethodAwaitBehavior Instance { get; } = new();

        public void OnCaptured(
            Playback playback,
            IPlaybackRunner runner,
            int checkpointId,
            PlaybackPromiseBase? promise,
            int? resumeScope
        )
        {
            if (promise?.Runner != null)
                promise.Runner.SetParentAwaitCheckpointId(checkpointId);
            promise!.AddRunnerContinuation(runner, checkpointId, runner.Epoch, resumeScope);
        }

        public void ArmPromise(
            Playback playback,
            TimelineCheckpoint checkpoint,
            PlaybackPromiseBase promise
        ) { }
    }

    internal sealed class YieldAwaitBehavior : IPlaybackAwaitBehavior
    {
        public static YieldAwaitBehavior Instance { get; } = new();

        public void ArmPromise(
            Playback playback,
            TimelineCheckpoint checkpoint,
            PlaybackPromiseBase promise
        )
        {
            playback.Post((PlaybackPromise)promise, CompletePromise);
        }
    }

    private sealed class DelayAwaitBehavior : IPlaybackAwaitBehavior
    {
        public static DelayAwaitBehavior Instance { get; } = new();

        public void ArmPromise(
            Playback playback,
            TimelineCheckpoint checkpoint,
            PlaybackPromiseBase promise
        )
        {
            var delay =
                promise.OwnerRecordIndex is { } delayIndex
                && playback.GetRecord(delayIndex) is var delayRecord
                && delayRecord.Behavior is TimelineRecordBehavior delayType
                && delayType.IsDelayRecord(delayRecord)
                    ? delayRecord
                    : throw new InvalidOperationException("Delay checkpoint has no delay record.");
            playback.recordRuntime.ArmDelay(delay.FlatIndex, (PlaybackPromise)promise);
            if (delay.Duration == TimeSpan.Zero)
                playback.Post(new PostedDelayCompletion(playback, delay.FlatIndex), CompleteDelay);
        }
    }

    private sealed class EffectAwaitBehavior : IPlaybackAwaitBehavior
    {
        public static EffectAwaitBehavior Instance { get; } = new();

        public void ArmPromise(
            Playback playback,
            TimelineCheckpoint checkpoint,
            PlaybackPromiseBase promise
        )
        {
            var effect =
                promise.OwnerRecordIndex is { } effectIndex
                && playback.GetRecord(effectIndex)
                    is { Behavior: EffectRecordBehavior } effectRecord
                    ? effectRecord
                    : throw new InvalidOperationException(
                        "Effect checkpoint has no effect record."
                    );
            if (playback.currentDirection == PlaybackDirection.Backward)
                effect.EffectBehavior.ReplayResult(promise);
            else
                playback.StartEffect(effect.Id, promise);
        }
    }

    private sealed class SeekLoopMoveNextAwaitBehavior : IPlaybackAwaitBehavior
    {
        public static SeekLoopMoveNextAwaitBehavior Instance { get; } = new();

        public void ArmPromise(
            Playback playback,
            TimelineCheckpoint checkpoint,
            PlaybackPromiseBase promise
        )
        {
            var ownerRecord = promise.OwnerRecordIndex is { } ownerIndex
                ? playback.GetRecord(ownerIndex)
                : (TimelineRecord?)null;
            if (
                ownerRecord is { } loopOwner
                && loopOwner.Behavior is TimelineRecordBehavior loopType
                && loopType.IsSeekLoopRecord(loopOwner)
            )
            {
                playback.recordRuntime.ArmSeekLoopMoveNext(
                    loopOwner.FlatIndex,
                    (PlaybackPromise<bool>)promise
                );
                if (!playback.activeSeekLoopIndexes.Contains(loopOwner.FlatIndex))
                    playback.activeSeekLoopIndexes.Add(loopOwner.FlatIndex);
                return;
            }
            if (
                ownerRecord is { } checkpointOwner
                && checkpointOwner.Behavior is TimelineRecordBehavior checkpointType
                && checkpointType.IsCheckpointRecord(checkpointOwner)
            )
            {
                playback.Post((PlaybackPromise<bool>)promise, CompleteBoolPromiseWithFalse);
                return;
            }
            throw new InvalidOperationException(
                "SeekLoopMoveNext promise has no supported owner record."
            );
        }
    }
}
