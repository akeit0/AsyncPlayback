namespace AsyncPlayback;

internal sealed class EffectRecordBehavior : TimelineRecordBehavior
{
    public EffectRecordBehavior(Func<CancellationToken, ValueTask<object?>> executeAsync)
        : base(TimelineRecordTypes.Effect.Id, TimelineRecordTypes.Effect.Name)
    {
        ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }

    public Func<CancellationToken, ValueTask<object?>> ExecuteAsync { get; }
    public object? Result { get; set; }
    public bool HasResult { get; set; }

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
        RecordEvaluationContext context,
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    )
    {
        return context.Playback.EmitEffectAtAsync(record, direction);
    }

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
}
