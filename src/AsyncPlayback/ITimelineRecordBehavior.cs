namespace AsyncPlayback;

public interface ITimelineRecordBehavior
{
    string TypeId { get; }
    string TypeName { get; }

    TimelineRecordVisibility GetVisibility(in TimelineRecord record);

    void AddBoundaries(in TimelineRecord record, TimelineBoundaryBuilder builder);

    void AddEvaluationEntries(in TimelineRecord record, TimelineEvaluationBuilder builder);

    bool IsReplayMatch(in TimelineRecord record, in TimelineRecordCreateRequest request);

    bool IsEvaluatable(in TimelineRecord record, in RecordEvaluationQuery query);

    ValueTask EvaluateAsync(
        Playback playback,
        TimelineRecord record,
        TimeSpan time,
        PlaybackDirection direction
    );

    TimelineRecordInfo ToInfo(in TimelineRecord record);
}
