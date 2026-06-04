# AsyncPlayback

AsyncPlayback experiment.
Just for fun.

```cs
using AsyncPlayback;

var playback = Playback.Start(Scenario);

Console.WriteLine("-- forward --");
while ((await playback.TryStepForwardAsync()).Moved)
{
    Console.WriteLine("th");
}

Console.WriteLine("-- back --");
while ((await playback.TryStepBackAsync()).Moved)
{
    Console.WriteLine("th");
}

static async PlaybackTask Scenario(Playback playback)
{
    for (var i = 0; i < 3; i++)
    {
        Console.Write(i);
        await playback.Checkpoint();
    }
}

```

```log
-- forward --
0th
1th
2th
-- back --
2th
1th
0th
```

## API notes

For the runtime model, timeline records, transport behavior, and TimeProvider
integration, see [doc/how-it-works.md](doc/how-it-works.md).

`TryStepForwardAsync()` and `TryStepBackAsync()` move to the next recorded await point.
Use logical granularity when you want user-facing steps and want to skip helper method
entry/call plumbing:

```cs
while ((await r.TryStepForwardAsync(PlaybackStepGranularity.Logical)).Moved)
{
    Console.WriteLine(r.CurrentRecord?.DebugLabel);
}
```

`StepResult` also reports the boundary that was reached:

```cs
var step = await r.TryStepBackAsync(PlaybackStepGranularity.Logical);
Console.WriteLine($"{step.DebugLabel} {step.BoundaryKind}");
```

Use `Effect` for real external async work. Effects are not automatically replayed
as recorded results. The workflow decides what to do from `CurrentDirection`, and
can use `Store`, `TryGet`, and `ClearStore` for manual state restoration:

```cs
static async PlaybackTask Scenario(Playback r)
{
    if (r.CurrentDirection == PlaybackDirection.Forward)
    {
        var receipt = await r.Effect(CreateOrderAsync, "create order");
        r.Store(receipt);
    }

    await r.Checkpoint("order created");

    if (r.CurrentDirection == PlaybackDirection.Backward &&
        r.TryGet<OrderReceipt>(out var receipt))
    {
        await r.Effect(() => CancelOrderAsync(receipt), "cancel order");
        r.ClearStore();
    }
}
```

`async PlaybackTask<T>` helper methods are supported:

```cs
static async PlaybackTask<int> LoadValue(Playback r)
{
    await r.Checkpoint("load value");
    return 42;
}
```

```cs
using AsyncPlayback;

var playback = Playback.Start(Scenario);

Console.WriteLine("-- forward realtime --");
for (var i = 0; i < 8; i++)
{
    await Task.Delay(TimeSpan.FromSeconds(0.25), playback.TimeProvider);
    await playback.AdvanceByElapsedTimeAsync();
}

Console.WriteLine("-- back commanded --");
for (var i = 0; i < 5; i++)
{
    await playback.MoveByAsync(-TimeSpan.FromSeconds(0.4));
    Console.WriteLine($"current time: {playback.Time}");
}

static async PlaybackTask Scenario(Playback playback)
{
    Console.WriteLine("Root started");

    await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(0.5)))
        Console.WriteLine("root loop A " + progress.Progress);

    await Child(playback);

    Console.WriteLine("Root resumed after child");

    await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(0.5)))
        Console.WriteLine("root loop B " + progress.Progress);

    Console.WriteLine("Root ended");
}

static async PlaybackTask Child(playback)
{
    Console.WriteLine("Child started");

    await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(1.0)))
        Console.WriteLine("child loop " + progress.Progress);

    Console.WriteLine("Child ended");
}

```

```log
-- forward --
Root started
root loop A 0.5
root loop A 1
Child started
child loop 0
child loop 0.25
child loop 0.5
child loop 0.75
child loop 1
Child ended
Root resumed after child
root loop B 0
root loop B 0.5
root loop B 1
Root ended
-- back --
root loop B 0.2
current time: 00:00:01.6000000
root loop B 0
Root resumed after child
Child ended
child loop 1
child loop 0.7
current time: 00:00:01.2000000
child loop 0.3
current time: 00:00:00.8000000
child loop 0
Child started
root loop A 1
root loop A 0.8
current time: 00:00:00.4000000
root loop A 0
Root started
current time: 00:00:00
```

## LICENSE

MIT
