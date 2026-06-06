namespace AsyncPlayback;

public readonly record struct StepResult(
    bool Moved,
    TimeSpan Time,
    long Timestamp,
    TimeSpan DeltaTime = default,
    TimelineRecordInfo? Record = null,
    PlaybackBoundaryKind? BoundaryKind = null
)
{
    public string? DebugLabel => Record?.DebugLabel;
}
