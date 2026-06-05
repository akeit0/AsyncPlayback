namespace AsyncPlayback;

public enum PlaybackEventKind
{
    RecordAdded,
    CheckpointAdded,
    BoundaryReached,
}

public readonly record struct PlaybackEvent(
    PlaybackEventKind Kind,
    TimelineRecordInfo Record,
    PlaybackBoundaryKind? BoundaryKind,
    PlaybackDirection Direction,
    TimeSpan Time,
    long Timestamp,
    TimeSpan DeltaTime,
    string DebugLabel
)
{
    public bool IsRecordAdded => Kind == PlaybackEventKind.RecordAdded;
    public bool IsCheckpointAdded => Kind == PlaybackEventKind.CheckpointAdded;
    public bool IsBoundaryReached => Kind == PlaybackEventKind.BoundaryReached;
    public bool IsStart => BoundaryKind == PlaybackBoundaryKind.Start;
    public bool IsEnd => BoundaryKind == PlaybackBoundaryKind.End;
    public bool IsPoint => BoundaryKind == PlaybackBoundaryKind.Point;
}
