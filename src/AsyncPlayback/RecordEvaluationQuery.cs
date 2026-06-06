namespace AsyncPlayback;

public readonly record struct RecordEvaluationQuery(
    TimeSpan Time,
    PlaybackDirection Direction,
    bool IncludeEnd
)
{
    internal Playback Playback { get; init; } = null!;
}
