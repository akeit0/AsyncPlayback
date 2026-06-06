namespace AsyncPlayback;

public enum TransportEvaluation
{
    TargetOnly,
    Traverse,
}

public enum PlaybackMoveMode
{
    TargetOnly,
    Traverse,
}

internal enum PlaybackTransportSource
{
    Move,
    Clock,
}

public readonly record struct TransportOptions(TransportEvaluation Evaluation, bool EvaluateTarget)
{
    public static TransportOptions TargetOnly { get; } = new(TransportEvaluation.TargetOnly, true);

    public static TransportOptions Traverse { get; } = new(TransportEvaluation.Traverse, true);
}
