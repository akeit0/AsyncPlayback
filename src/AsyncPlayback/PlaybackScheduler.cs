using System.Runtime.CompilerServices;

namespace AsyncPlayback;

internal sealed class PlaybackScheduler
{
    private readonly object gate = new();
    private readonly Queue<ScheduledAction> ready = [];
    private readonly SemaphoreSlim readySignal = new(0);

    private long generation;
    private int pendingExternalEffects;

    public bool HasReady
    {
        get
        {
            lock (gate)
                return ready.Count != 0;
        }
    }

    public bool HasPendingExternalEffects => Volatile.Read(ref pendingExternalEffects) != 0;

    public void Post<TState>(TState state, Action<TState> action)
        where TState : class
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        var objectAction = Unsafe.As<Action<object>>(action);
        var capturedGeneration = generation;
        lock (gate)
            ready.Enqueue(new(state, objectAction, capturedGeneration));

        readySignal.Release();
    }

    public bool TryRunOneReady()
    {
        ScheduledAction scheduled;
        lock (gate)
        {
            if (ready.Count == 0)
                return false;

            scheduled = ready.Dequeue();
        }

        if (scheduled.Generation == generation)
            scheduled.Action(scheduled.State);

        return true;
    }

    public void ResetReady()
    {
        lock (gate)
            ready.Clear();
        unchecked
        {
            generation++;
        }
    }

    public void BeginExternalEffect()
    {
        Interlocked.Increment(ref pendingExternalEffects);
    }

    public void EndExternalEffect()
    {
        Interlocked.Decrement(ref pendingExternalEffects);
        readySignal.Release();
    }

    public ValueTask WaitForWorkAsync(CancellationToken cancellationToken)
    {
        return new(readySignal.WaitAsync(cancellationToken));
    }

    private readonly record struct ScheduledAction(
        object State,
        Action<object> Action,
        long Generation
    );
}
