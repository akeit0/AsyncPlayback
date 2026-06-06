namespace AsyncPlayback;

internal sealed class CallRecordBehavior : TimelineRecordBehavior
{
    public CallRecordBehavior(IPlaybackRunner parentRunner, IPlaybackRunner childRunner)
        : base(TimelineRecordTypes.Call.Id, TimelineRecordTypes.Call.Name)
    {
        ParentRunner = parentRunner;
        ChildRunner = childRunner;
    }

    public IPlaybackRunner ParentRunner { get; set; }
    public IPlaybackRunner ChildRunner { get; set; }
    public int ParentAwaitCheckpointId { get; set; }

    public override TimelineRecordVisibility GetVisibility(in TimelineRecord record)
    {
        return TimelineRecordVisibility.Infrastructure;
    }

    public override void AddBoundaries(in TimelineRecord record, TimelineBoundaryBuilder builder)
    {
        if (builder.Scope != TimelineBoundaryScope.Traversal)
            return;

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
}
