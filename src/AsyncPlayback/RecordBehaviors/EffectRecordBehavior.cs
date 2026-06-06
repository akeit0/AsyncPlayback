namespace AsyncPlayback;

internal abstract class EffectRecordBehavior : TimelineRecordBehavior
{
    protected EffectRecordBehavior()
        : base(TimelineRecordTypes.Effect.Id, TimelineRecordTypes.Effect.Name) { }

    public override void AddBoundaries(in TimelineRecord record, TimelineBoundaryBuilder builder)
    {
        builder.AddStart(record);
        builder.AddEnd(record);
    }

    public override bool IsReplayMatch(
        in TimelineRecord record,
        in TimelineRecordCreateRequest request
    )
    {
        return IsRequestedBehavior(record, request)
            && record.StartTime == request.StartTime
            && record.DebugLabel == request.DebugLabel;
    }

    public override bool IsEvaluatable(in TimelineRecord record, in RecordEvaluationQuery query)
    {
        return record.StartTime == query.Time;
    }

    public override ValueTask EvaluateAsync(
        Playback playback,
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        return EvaluateEffectAsync(playback, record, direction);
    }

    internal abstract Task RunAsync(
        Playback playback,
        RecordId recordId,
        PlaybackPromiseBase promise,
        long startTimestamp,
        PlaybackDirection direction,
        CancellationToken cancellationToken
    );

    internal abstract void ReplayResult(PlaybackPromiseBase promise);

    internal override TimelineBoundary? GetCurrentBoundaryPosition(
        in TimelineRecord record,
        TimeSpan time
    )
    {
        return GetTimedBoundaryPosition(record, time);
    }

    internal override bool ShouldEmitTimedRecordStart(in TimelineRecord record)
    {
        return true;
    }

    private static async ValueTask EvaluateEffectAsync(
        Playback playback,
        TimelineRecord effect,
        PlaybackDirection direction
    )
    {
        if (direction == PlaybackDirection.Backward)
        {
            playback.MoveTimeTo(effect.StartTime);
            playback.RestoreEntryState(effect.FlatIndex);
            playback.SetCurrentRecord(effect.Id);
            playback.EnterPlaybackAfterRecord(effect.FlatIndex);
            playback.EmitBoundaryReached(effect.Id, TimelineBoundaryKind.Start, effect.StartTime);
            return;
        }

        playback.RestoreToRecord(effect.Id);
        await playback.RunReadyAsync(playback.CurrentCancellationToken).ConfigureAwait(false);
        playback.SetCurrentRecord(effect.Id);
        playback.EmitBoundaryReached(effect.Id, TimelineBoundaryKind.Start, effect.StartTime);
    }
}

internal sealed class VoidEffectRecordBehavior : EffectRecordBehavior
{
    private readonly Func<CancellationToken, ValueTask> executeAsync;

    public VoidEffectRecordBehavior(Func<CancellationToken, ValueTask> executeAsync)
    {
        this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }

    internal override async Task RunAsync(
        Playback playback,
        RecordId recordId,
        PlaybackPromiseBase promise,
        long startTimestamp,
        PlaybackDirection direction,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await executeAsync(cancellationToken).ConfigureAwait(false);
            var endTimestamp = playback.TimeProvider.GetTimestamp();
            playback.Post(
                new VoidEffectCompletion(
                    playback,
                    recordId,
                    promise,
                    startTimestamp,
                    endTimestamp,
                    direction
                ),
                static completion => completion.Complete()
            );
        }
        catch (Exception exception)
        {
            playback.PostEffectFailure(recordId, promise, startTimestamp, direction, exception);
        }
        finally
        {
            playback.EndExternalEffect();
        }
    }

    internal override void ReplayResult(PlaybackPromiseBase promise)
    {
        promise.TrySetObjectResult(null);
    }

    private sealed record VoidEffectCompletion(
        Playback Playback,
        RecordId RecordId,
        PlaybackPromiseBase Promise,
        long StartTimestamp,
        long EndTimestamp,
        PlaybackDirection Direction
    )
    {
        public void Complete()
        {
            Playback.CompleteEffectRecord(
                RecordId,
                Promise,
                StartTimestamp,
                EndTimestamp,
                Direction
            );
            Promise.TrySetObjectResult(null);
        }
    }
}

internal sealed class EffectRecordBehavior<T> : EffectRecordBehavior
{
    private readonly Func<CancellationToken, ValueTask<T>> executeAsync;
    private T? result;
    private bool hasResult;

    public EffectRecordBehavior(Func<CancellationToken, ValueTask<T>> executeAsync)
    {
        this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }

    internal override async Task RunAsync(
        Playback playback,
        RecordId recordId,
        PlaybackPromiseBase promise,
        long startTimestamp,
        PlaybackDirection direction,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var value = await executeAsync(cancellationToken).ConfigureAwait(false);
            var endTimestamp = playback.TimeProvider.GetTimestamp();
            playback.Post(
                new TypedEffectCompletion(
                    playback,
                    recordId,
                    (PlaybackPromise<T>)promise,
                    startTimestamp,
                    endTimestamp,
                    direction,
                    this,
                    value
                ),
                static completion => completion.Complete()
            );
        }
        catch (Exception exception)
        {
            playback.PostEffectFailure(recordId, promise, startTimestamp, direction, exception);
        }
        finally
        {
            playback.EndExternalEffect();
        }
    }

    internal override void ReplayResult(PlaybackPromiseBase promise)
    {
        if (!hasResult)
            throw new InvalidOperationException("Effect record has no recorded result.");

        ((PlaybackPromise<T>)promise).TrySetResult(result!);
    }

    private sealed record TypedEffectCompletion(
        Playback Playback,
        RecordId RecordId,
        PlaybackPromise<T> Promise,
        long StartTimestamp,
        long EndTimestamp,
        PlaybackDirection Direction,
        EffectRecordBehavior<T> Behavior,
        T Result
    )
    {
        public void Complete()
        {
            Playback.CompleteEffectRecord(
                RecordId,
                Promise,
                StartTimestamp,
                EndTimestamp,
                Direction
            );
            Behavior.result = Result;
            Behavior.hasResult = true;
            Promise.TrySetResult(Result);
        }
    }
}
