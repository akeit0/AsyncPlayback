# How AsyncPlayback Works

AsyncPlayback makes a C# async workflow movable on a virtual timeline. It does
not execute C# code backward. Rewind and seek work by restoring previously
captured async state machine snapshots, restoring the library-owned playback
state for that point, then evaluating the timeline in the requested direction.

The central idea is small:

```csharp
static async PlaybackTask Scenario(Playback playback)
{
    await PlaybackTask.Checkpoint("start");
    await PlaybackTask.Delay(TimeSpan.FromSeconds(1), "wait");
    await PlaybackTask.Checkpoint("end");
}
```

The method looks like normal async C#, but it returns `PlaybackTask`, not
`Task`. That changes which async method builder the compiler uses.

## The C# Part

`PlaybackTask` is marked with `AsyncMethodBuilder`:

```csharp
[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder))]
public readonly struct PlaybackTask
{
    // wraps a PlaybackPromise
}
```

When the C# compiler sees an `async PlaybackTask` method, it still emits the
usual state machine: fields for locals, a field for the current state, and a
`MoveNext()` method. The difference is that awaits go through
`PlaybackTaskMethodBuilder` instead of `AsyncTaskMethodBuilder`.

At a playback await point, the builder receives the generated state machine by
`ref`:

```csharp
public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
    ref TAwaiter awaiter,
    ref TStateMachine stateMachine
)
    where TAwaiter : ICriticalNotifyCompletion, IPlaybackAwaiter
    where TStateMachine : IAsyncStateMachine
{
    core.CaptureAwait(ref awaiter, ref stateMachine);
}
```

That `ref stateMachine` is what makes this library possible. At the await point,
the state machine already contains the program counter and current local values.
AsyncPlayback copies that state machine and keeps the copy as a checkpoint.

For value-type state machines, the copy is a normal value copy. For reference
type state machines, AsyncPlayback uses `MemberwiseClone`. This is a shallow
snapshot: local references still point to the same objects. The library restores
the async control flow and field values held by the state machine; it does not
deep-clone arbitrary object graphs.

## Runners

Each running async method has a `PlaybackRunner<TStateMachine>`. The runner owns
the current state machine and its checkpoint snapshots.

Conceptually it stores this:

```csharp
private TStateMachine current;
private TStateMachine[] checkpoints;
private int suspendedCheckpointId;
```

When an await point is reached:

1. The runner copies the state machine into `checkpoints`.
2. The await point gets a checkpoint id.
3. The matching timeline record stores a `TimelineCheckpoint`.
4. Execution stops until playback decides to resume it.

Restoring a checkpoint is the opposite:

```csharp
current = SnapshotStateMachine(checkpoints[checkpointId - 1]);
suspendedCheckpointId = checkpointId;
isCompleted = false;
```

After restoration, calling `MoveNext()` continues from that captured await point,
because that is how C# async state machines represent suspension.

## Timeline Records

The state machine snapshot says "where the code can resume". Timeline records
say "where that await point lives on playback time".

AsyncPlayback records entries such as:

- `CheckpointTimelineRecord`: a zero-duration await point.
- `DelayRecord`: a virtual duration.
- `SeekLoopRecord`: a virtual duration whose body can be evaluated at arbitrary
  progress.
- `EffectRecord`: external async work.
- `CallTimelineRecord`: a nested `async PlaybackTask` call.

A record has a start time, duration, label, parent/child relationship, and often
an entry checkpoint:

```csharp
internal abstract class TimelineRecord
{
    public TimeSpan StartTime { get; }
    public TimeSpan Duration { get; }
    public TimelineCheckpoint? EntryCheckpoint { get; internal set; }
}
```

The timeline is recorded as the workflow first runs. Later movement reuses those
records when the workflow shape still matches. If replay reaches code that now
records a different shape, records after that point are truncated and rebuilt.
That keeps the timeline tied to the current execution path.

## Why Rewind Works

Rewind does not call an inverse of each statement. Instead, playback finds a
record or checkpoint at the target area, restores the runner tree to the saved
checkpoint, restores playback-owned state, and then evaluates the requested
position.

For example:

```csharp
await playback.MoveToAsync(TimeSpan.FromSeconds(2));
await playback.MoveByAsync(-TimeSpan.FromSeconds(0.4));
```

The backward move:

1. Chooses backward traversal because the target time is less than the current
   time.
2. Walks timeline boundaries between the current time and target time.
3. Restores checkpoints when needed.
4. Evaluates records that are meaningful at those boundaries.
5. Sets `Playback.Time` to the final target.

This is why a nested child method can be rewound without calling the child body
twice by accident. A child call and the parent continuation each have their own
records and checkpoints. Backward traversal must restore the right checkpoint;
it must not treat internal continuation checkpoints as user-visible child entry
points.

## Nested Async Calls

Nested `PlaybackTask` methods create nested runners.

```csharp
static async PlaybackTask Parent(Playback playback)
{
    await Child(playback);
    await PlaybackTask.Checkpoint("after child");
}

static async PlaybackTask Child(Playback playback)
{
    await PlaybackTask.Delay(TimeSpan.FromSeconds(1));
}
```

When `Parent` awaits `Child`, the parent runner captures the await point and the
child gets its own runner. A `CallTimelineRecord` connects them. To restore a
checkpoint inside the child, AsyncPlayback rebuilds the runner chain from root
to child:

```text
Parent runner -> Child runner -> target checkpoint
```

The parent is restored to the await that was waiting for the child, then the
child is restored to its target checkpoint. The promise continuation is
reconnected so that, if the child completes again, the parent can continue from
the correct await point.

## Delay Is Virtual

`PlaybackTask.Delay(duration)` is not `Task.Delay(duration)`. It records a timed
segment on the virtual timeline.

```csharp
await PlaybackTask.Delay(TimeSpan.FromSeconds(1));
```

Moving from `00:00:00` to `00:00:00.5` puts playback halfway through that
record. Moving to `00:00:01` reaches its end and lets the state machine resume
after the await.

This is why delays can be skipped, rewound, and seeked without sleeping.

## Seek Loops

`ForEachOnSeek` is the primitive for code that should run for intermediate
positions, not only at await boundaries.

```csharp
await foreach (var frame in playback.ForEachOnSeek(TimeSpan.FromSeconds(1)))
{
    Render(frame.Progress);
}
```

The loop record has a duration. During movement, AsyncPlayback computes progress
from `Playback.Time`:

```text
progress = (current time - record start) / record duration
```

The body can run for the target position and for traversed intermediate
positions. That makes scrubber-style behavior possible without requiring a fixed
frame rate inside the library.

## Stored State

AsyncPlayback restores async control flow automatically, but user-visible state
often lives outside local variables. The library provides one explicit store for
that case:

```csharp
playback.Store(new SelectionState(id));

if (playback.TryGet<SelectionState>(out var state))
{
    RestoreSelection(state.Id);
}
```

Each `TimelineCheckpoint` contains a `StoreSnapshot`:

```csharp
internal sealed class TimelineCheckpoint
{
    public StoreSnapshot StoreSnapshot { get; internal set; }
}
```

When playback restores a checkpoint, it restores that store snapshot too. The
store is intentionally simple: one current value, not a dictionary and not a
deep clone. If the stored object is mutable, the snapshot stores the object
reference. Use immutable values or replace the stored value when state changes.

External side effects are not undone. If a workflow writes to a database, sends
a message, or mutates UI outside the state machine, the workflow must model the
forward and backward behavior explicitly.

## Effects

Effects are for real asynchronous work:

```csharp
var data = await playback.Effect(
    ct => LoadDataAsync(id, ct),
    "load data"
);
```

An effect is a timeline record, but AsyncPlayback does not pretend the external
world is rewindable. When an effect runs while playback is moving forward,
AsyncPlayback measures provider elapsed time around the external work and uses
that elapsed time as the effect record duration. If the effect takes 300 ms of
provider time, the effect occupies 300 ms of virtual playback time.

Backward compensation effects are different. They can perform external restore
work, and `DeltaTime` still reports provider elapsed time, but they do not add a
positive span to the forward virtual timeline. Code that needs deterministic
rewind should store the data needed to restore state, and should check playback
direction when deciding what to do.

## TimeProvider

TimeProvider integration is deliberately small. Playback does not own a frame
loop. User code owns cadence; playback samples elapsed provider time when asked.

```csharp
var playback = Playback.Start(Scenario, TimeProvider.System);

while (!playback.IsCompleted)
{
    await Task.Delay(TimeSpan.FromMilliseconds(250), playback.TimeProvider);
    await playback.AdvanceByElapsedTimeAsync();
}
```

Forward effects also sample the same provider on completion. That is why
external work represented by `Effect` can occupy real elapsed duration in the
timeline, while commanded movement can still jump without waiting.

`AdvanceByElapsedTimeAsync()` samples `playback.TimeProvider`, computes
`DeltaTime`, and then moves virtual time forward by that delta. Reverse
real-time playback uses the same idea:

```csharp
await playback.RewindByElapsedTimeAsync();
```

Commanded movement does not wait for real time:

```csharp
await playback.MoveByAsync(-TimeSpan.FromSeconds(0.4));
await playback.MoveToAsync(TimeSpan.FromSeconds(2));
await playback.MoveToAsync(TimeSpan.FromSeconds(2), PlaybackMoveMode.TargetOnly);
```

`MoveToAsync` is the primary commanded movement API. `PlaybackMoveMode.Traverse`
walks timeline boundaries between the current time and target time, while
`PlaybackMoveMode.TargetOnly` evaluates only the requested target. Pass
`evaluateTarget: false` to reposition without evaluating the target record.

Tests should use `FakeTimeProvider` from
`Microsoft.Extensions.TimeProvider.Testing`:

```csharp
using Microsoft.Extensions.Time.Testing;

var time = new FakeTimeProvider();
var playback = Playback.Start(Scenario, time);

time.Advance(TimeSpan.FromMilliseconds(250));
await playback.AdvanceByElapsedTimeAsync();
```

No sleeping is needed, and timer-based code using the same provider can be
advanced deterministically.

## Limits

The library rewinds playback-managed execution state. It does not make the
whole CLR process rewindable.

- State machine snapshots are shallow.
- External side effects are not automatically undone.
- Mutable objects referenced by locals or by the store are not deep-copied.
- Workflow methods should await playback primitives or other `PlaybackTask`
  methods, so the custom builder can capture the await points.

These limits are also why the API keeps time movement explicit. The application
decides when real time passes, when commanded movement happens, and what external
state should be restored at each point.
