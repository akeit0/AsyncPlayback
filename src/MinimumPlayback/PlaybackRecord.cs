namespace MinimumPlayback;

public enum PlaybackRecordRole
{
    Checkpoint,
    Call,
}

public readonly record struct PlaybackRecord(
    int Index,
    PlaybackRecordRole Role,
    string Label,
    int Depth,
    int ParentIndex
);
