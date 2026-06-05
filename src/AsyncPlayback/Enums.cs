namespace AsyncPlayback;

public enum TimelineRecordKind
{
    Checkpoint,
    Delay,
    Effect,
    SeekLoop,
    Call,
}

public enum TimelineRecordVisibility
{
    Workflow,
    Infrastructure,
}

public enum CheckpointRecordKind
{
    Entry,
    User,
    Implicit,
}

public enum PlaybackPromiseKind
{
    AsyncMethod,
    Delay,
    Effect,
    Yield,
    Checkpoint,
    SeekLoopMoveNext,
}

public enum PlaybackMode
{
    Recording,
    Playback,
}

public enum PlaybackDirection
{
    Forward,
    Backward,
}

public enum PlaybackStepGranularity
{
    AwaitPoint,
    Logical,
}

public enum PlaybackBoundaryKind
{
    Point,
    Start,
    End,
}
