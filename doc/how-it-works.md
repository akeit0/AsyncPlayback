# How AsyncPlayback Works

AsyncPlayback runs an `async PlaybackTask` method as a replayable timeline. The
workflow is ordinary C# async code, but its await points are handled by
AsyncPlayback's custom task method builder instead of the normal `Task` builder.
That lets the library snapshot the generated async state machine at each
playback await point and later restore it when the transport moves forward,
backward, or seeks to a target time.

## Main Concepts

`Playback` is the transport and timeline owner. It stores the current virtual
`Time`, the requested `TargetTime`, the current direction, and the recorded
timeline records.

`PlaybackTask` and `PlaybackTask<T>` are the async return types used by workflow
methods. Methods returning these types are driven by `PlaybackTaskMethodBuilder`,
which creates a runner for the compiler-generated state machine.

`PlaybackRunner<TStateMachine>` owns snapshots of one async state machine. When
an await point is reached, the runner clones the state machine and stores it as a
checkpoint. Restoring a checkpoint replaces the runner's current state machine
with that snapshot and resumes from the captured await.

`TimelineRecord` entries describe the replayable timeline. User-facing records
include checkpoints, delays, effects, seek loops, and nested async calls. Records
carry start time, duration, debug labels, parent-child structure, and restore
checkpoints.

## Recording and Playback

A playback starts in recording mode:

```csharp
var playback = Playback.Start(Scenario);
```

`Start` constructs a `Playback`, starts the root workflow, and runs until the
workflow reaches its first playback await point. Each await point records enough
information to continue later:

```csharp
static async PlaybackTask Scenario(Playback playback)
{
    await playback.Checkpoint("start");
    await playback.Delay(TimeSpan.FromSeconds(1), "wait");
    await playback.Checkpoint("end");
}
```

Moving the transport replays existing records when they still match the workflow
shape. If the workflow takes a different branch while replaying, the old records
after that point are truncated and new records are recorded from the current
position. This keeps the timeline consistent with the latest execution path.

## Await Points

`Checkpoint` is a zero-duration explicit await point. It is useful for logical
steps such as "order created" or "form validated".

`Delay` is virtual time, not wall-clock sleeping. It records a duration and
completes when playback time reaches the delay end.

`ForEachOnSeek` records a seek loop over a virtual duration. The loop body runs
at the current playback time and receives progress derived from that time. This
is the main primitive for animation-like or scrubber-like behavior.

`Effect` is real external async work. Effects are not memoized as recorded
results. The workflow decides when to run effects, usually by checking
`CurrentDirection` and using `Store`/`TryGet` to remember data needed for reverse
or restore behavior.

## Transport

The transport can move by explicit timeline steps:

```csharp
await playback.TryStepForwardAsync();
await playback.TryStepBackAsync();
```

It can also move by virtual time:

```csharp
await playback.MoveByAsync(TimeSpan.FromMilliseconds(250));
await playback.MoveToAsync(TimeSpan.FromSeconds(2));
await playback.SeekToAsync(TimeSpan.FromSeconds(2));
```

`TransportOptions.Traverse` evaluates boundaries between the current time and
the target. `TransportOptions.TargetOnly` jumps to the target and evaluates only
that target position.

## TimeProvider Integration

`Playback` works in virtual time, but it can sample real time from a
`TimeProvider` whenever the workflow reaches a playback await point:

```csharp
var playback = Playback.Start(Scenario, TimeProvider.System);
```

`Playback.Timestamp` is the last raw timestamp read from the provider.
`Playback.DeltaTime` is the elapsed real time between the previous sampled
timestamp and the latest await point. Step APIs return both values, and the same
metadata is available on `TimelineRecordInfo` for records that have an entry
checkpoint:

```csharp
var step = await playback.TryStepForwardAsync();
Console.WriteLine(step.DeltaTime);
Console.WriteLine(step.Timestamp);
Console.WriteLine(step.Record?.DeltaTime);
```

Real-time movement is explicit. User code owns the loop cadence, and `Playback`
samples the provider when asked to move by elapsed time:

```csharp
await Task.Delay(TimeSpan.FromMilliseconds(250), playback.TimeProvider);
await playback.AdvanceByElapsedTimeAsync();
```

For reverse playback at elapsed-time rate, call
`RewindByElapsedTimeAsync()`. For commanded navigation that should not wait for
real time, use `MoveByAsync`, `MoveToAsync`, `SeekToAsync`, or the step APIs.

For tests, use `FakeTimeProvider` from
`Microsoft.Extensions.TimeProvider.Testing`. The fake replaces the timestamp
source, so tests can advance real elapsed time deterministically without
sleeping:

```csharp
using Microsoft.Extensions.Time.Testing;

var timeProvider = new FakeTimeProvider();
var playback = Playback.Start(Scenario, timeProvider);

timeProvider.Advance(TimeSpan.FromMilliseconds(250));
var step = await playback.TryStepForwardAsync();

Assert.Equal(TimeSpan.FromMilliseconds(250), step.DeltaTime);
```

## State and Restoration

`Store`, `TryGet`, and `ClearStore` manage one user-owned stored object. The
store is not type-keyed; it is a single current value. Store snapshots are
captured at timeline checkpoints so moving backward or seeking can restore the
stored value that belonged to the restored position.

This is intentionally explicit. AsyncPlayback restores async state machines and
the stored object, but it does not automatically undo external side effects.
Workflows that touch external systems should model forward and backward behavior
with `CurrentDirection`, `Effect`, and stored data.
