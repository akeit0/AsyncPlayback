namespace AsyncPlayback;

internal interface IRecordPayload { }

internal sealed class EffectRecordPayload : IRecordPayload
{
    public EffectRecordPayload(Func<CancellationToken, ValueTask<object?>> executeAsync)
    {
        ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }

    public Func<CancellationToken, ValueTask<object?>> ExecuteAsync { get; }
    public object? Result { get; set; }
    public bool HasResult { get; set; }
}

internal sealed class CallRecordPayload : IRecordPayload
{
    public CallRecordPayload(IPlaybackRunner parentRunner, IPlaybackRunner childRunner)
    {
        ParentRunner = parentRunner;
        ChildRunner = childRunner;
    }

    public IPlaybackRunner ParentRunner { get; set; }
    public IPlaybackRunner ChildRunner { get; set; }
    public int ParentAwaitCheckpointId { get; set; }
}
