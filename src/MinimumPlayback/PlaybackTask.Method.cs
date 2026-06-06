namespace MinimumPlayback;

public readonly partial struct PlaybackTask
{
    public static CheckpointAwaitable Checkpoint(string label = "Checkpoint")
    {
        return PlaybackRuntime.CurrentPlayback!.Checkpoint(label);
    }
}
