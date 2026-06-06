namespace AsyncPlayback;

public sealed class RecordEvaluationContext
{
    internal RecordEvaluationContext(Playback playback)
    {
        Playback = playback;
    }

    internal Playback Playback { get; }

    public void MarkEvaluated(
        TimelineRecord record,
        PlaybackBoundaryKind? boundaryKind,
        TimeSpan time
    )
    {
        Playback.MarkRecordEvaluated(record, boundaryKind, time);
    }
}
