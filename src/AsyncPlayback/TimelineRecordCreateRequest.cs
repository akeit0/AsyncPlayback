namespace AsyncPlayback;

public readonly record struct TimelineRecordCreateRequest(
    ITimelineRecordBehavior Behavior,
    TimeSpan StartTime,
    TimeSpan Duration,
    string DebugLabel
);
