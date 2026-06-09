# MinimumPlayback Internal Algorithm

This document explains the internal model used by `src/MinimumPlayback`.
It is intentionally not an API guide.  The goal is to describe how the
implementation uses C# async-method lowering as a resumable state-machine
runtime, and how that runtime is recorded into a small playback timeline.

`MinimumPlayback` is an experiment.  It keeps only the pieces needed to study
checkpoint restore, nested async calls, typed return values, call-end stops, and
backward movement.  It has no virtual time, no scheduler, no `ValueTask`, no
general-purpose `Task` semantics, and no user data store.

## A Small Example

Consider this playback program:

```csharp
using MinimumPlayback;
using static MinimumPlayback.PlaybackTask;

var playback = Playback.Create(ForScenario);

Console.WriteLine("-- forward --");
while (playback.TryMoveNext())
{
    Console.WriteLine();
}

Console.WriteLine("-- back --");
while (playback.TryMoveBack())
{
    Console.WriteLine();
}

Console.WriteLine("-- forward --");
while (playback.TryMoveNext())
{
    Console.WriteLine();
}

static async PlaybackTask ForScenario(Playback playback)
{
    for (var i = 0; i < 3; i++)
    {
        Console.Write(i);
        await Checkpoint();
    }

    Console.Write("done");
}
```

The output is:

```text
-- forward --
0
1
2
done
-- back --
done
2
1
0
-- forward --
0
1
2
done
```

This is the behavior the project exists to study.  Backward movement is not C#
running in reverse.  It is forward execution from an earlier state-machine
snapshot.

Forward movement restores the current stop, executes the generated
`MoveNext()` until the next stop, then appends or advances to that stop.
Backward movement restores the previous stop, executes the same generated
`MoveNext()` forward until the current stop is reproduced, consumes that
current stop, and leaves the cursor at the previous stop.

## Core Idea

C# already compiles an `async` method into a state machine.  That state machine
has a `MoveNext()` method and fields for locals, awaiters, and the current
state.

`MinimumPlayback` uses that compiler-generated state machine as the executable
state.  It captures shallow snapshots of state machines at selected boundaries
and stores those snapshots as runner-local stops.

The timeline is not the executable state.  The executable state lives in
`PlaybackRunner<TStateMachine>`:

```text
runner
  current state-machine snapshot
  initial state-machine snapshot
  stop snapshots[]
```

The timeline is an indexed debug and movement log:

```text
record index -> record metadata
record index -> runner, for runner-backed boundaries
record index -> runner-local stop id, for snapshot-backed boundaries
```

Movement restores a runner stop, runs `MoveNext()`, and either records new
records or consumes existing records during replay.

```mermaid
flowchart LR
    RecordIndex["record index"]
    Record["records[index]<br/>role, label, depth, parent"]
    RunnerMap["runner = recordRunners[index]<br/>for runner-backed boundaries"]
    StopMap["stopId = stopIdsByRecord[index]<br/>for snapshot-backed boundaries"]
    RunnerStop["runner.stops[stopId]<br/>state-machine snapshot"]

    RecordIndex --> Record
    RecordIndex --> RunnerMap
    RecordIndex --> StopMap
    RunnerMap --> RunnerStop
    StopMap --> RunnerStop
```

## Generated State Machine Shape

The compiler lowers the sample loop into one state machine.  The exact generated
names do not matter; the important part is that locals and the resume point are
stored as fields, and `MoveNext()` is the only execution entry point.

```csharp
[StructLayout(LayoutKind.Auto)]
[CompilerGenerated]
private struct ForScenarioStateMachine : IAsyncStateMachine
{
    public int state;
    public PlaybackTaskMethodBuilder builder;

    private int i;
    private CheckpointAwaitable.Awaiter checkpointAwaiter;

    private void MoveNext()
    {
        var num = state;

        try
        {
            if (num != 0)
            {
                i = 0;
                goto LoopTest;
            }

            var awaiter = checkpointAwaiter;
            checkpointAwaiter = default;
            num = state = -1;

        ResumeAfterCheckpoint:
            awaiter.GetResult();
            i++;

        LoopTest:
            if (i < 3)
            {
                Console.Write(i);
                awaiter = PlaybackTask.Checkpoint().GetAwaiter();
                if (!awaiter.IsCompleted)
                {
                    num = state = 0;
                    checkpointAwaiter = awaiter;
                    builder.AwaitUnsafeOnCompleted(ref awaiter, ref this);
                    return;
                }

                goto ResumeAfterCheckpoint;
            }

            Console.Write("done");
        }
        catch (Exception exception)
        {
            state = -2;
            builder.SetException(exception);
            return;
        }

        state = -2;
        builder.SetResult();
    }

    void IAsyncStateMachine.MoveNext()
    {
        MoveNext();
    }

    void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)
    {
        builder.SetStateMachine(stateMachine);
    }
}
```

The relevant fields are:

```text
state              compiler resume point
i                  user loop local
checkpointAwaiter  awaiter field held across suspension
builder            custom PlaybackTask builder
```

## State-Machine Transition Example

For this loop, the meaningful state is the pair:

```text
(state, i)
```

`state` is the compiler resume point.  `i` is the user loop local.  The stored
playback stops are snapshots of that pair:

```text
initial   = (state=-1, i=undefined)
stop0     = (state=0,  i=0)
stop1     = (state=0,  i=1)
stop2     = (state=0,  i=2)
Completed = (state=-2, i=3)
```

Direction changes timeline bookkeeping, not the generated `MoveNext()`
direction.

```mermaid
flowchart TD
    I["initial<br/>(state=-1)"]
    S0["stop0<br/>(state=0, i=0)"]
    S1["stop1<br/>(state=0, i=1)"]
    S2["stop2<br/>(state=0, i=2)"]
    C["Completed<br/>(state=-2, i=3)"]

    I -->|"MoveNext entry<br/>write 0"| S0
    S0 -->|"MoveNext resume<br/>i++ ; write 1"| S1
    S1 -->|"MoveNext resume<br/>i++ ; write 2"| S2
    S2 -->|"MoveNext resume<br/>i++ ; write done<br/>SetResult"| C

    S0 -. "back: replay initial to stop0<br/>cursor becomes -1" .-> I
    S1 -. "back: replay stop0 to stop1<br/>cursor becomes 0" .-> S0
    S2 -. "back: replay stop1 to stop2<br/>cursor becomes 1" .-> S1
    C -. "back: replay stop2 to Completed<br/>cursor becomes 2" .-> S2
```

Read each solid arrow as a forward `MoveNext` transition.  Read each dotted
arrow upward as a `MoveBack` operation: it restores the previous stop, executes
the same generated transition forward to reproduce the current stop, consumes
that existing stop, then leaves the cursor at the previous stop.

## Nested Call Example

Now consider a nested task with simple logging:

```csharp
var playback = Playback.Create(_ => Nest(3, 3));

static async PlaybackTask Nest(int m, int n)
{
    Console.WriteLine($"Entering nest({n})" + (IsForward ? " forward" : " backward"));

    if (n <= 0)
        return;

    for (var i = n; i < m; i++)
    {
        await Checkpoint();
        Console.WriteLine($"In nest({n}) loop {i}" + (IsForward ? " forward" : " backward"));
    }

    await Nest(m, n - 1);

    Console.WriteLine($"Exiting nest({n})" + (IsForward ? " forward" : " backward"));
}
```

The important point is that each `Nest(...)` call becomes a child runner.  A
`Call` record is the movement boundary before that child runner starts.

Forward execution prints:

```text
Entering nest(3) forward
Entering nest(2) forward
In nest(2) loop 2 forward
Entering nest(1) forward
In nest(1) loop 1 forward
In nest(1) loop 2 forward
Entering nest(0) forward
Exiting nest(1) forward
Exiting nest(2) forward
Exiting nest(3) forward
```

Moving backward from completion replays the same segments with `IsForward ==
false`:

```text
Exiting nest(3) backward
Exiting nest(2) backward
Exiting nest(1) backward
Entering nest(0) backward
In nest(1) loop 2 backward
In nest(1) loop 1 backward
Entering nest(1) backward
In nest(2) loop 2 backward
Entering nest(2) backward
Entering nest(3) backward
```

`Checkpoint`, `Call`, `CallEnd`, and `Completed` are movement stops.  `Call`
records are child-entry boundaries: movement can land on a call before the child
state machine starts.

The resulting timeline is:

```text
  0: In depth=0 parent=-1
  1: | Checkpoint depth=1 parent=0
  2: | In depth=1 parent=0
  3: | | Checkpoint depth=2 parent=2
  4: | | Checkpoint depth=2 parent=2
  5: | | In depth=2 parent=2
  6: | | Out depth=2 parent=5
  7: | Out depth=1 parent=2
  8: Out depth=0 parent=0
  9: Completed depth=0 parent=-1
```

## Async Builder Hook

The custom async method builder is selected by:

```csharp
[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder))]
public readonly partial struct PlaybackTask
```

and:

```csharp
[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder<>))]
public readonly struct PlaybackTask<T>
```

That means the compiler-generated state machine calls
`PlaybackTaskMethodBuilder` instead of the normal `AsyncTaskMethodBuilder`.
The important callbacks are:

```text
Start(ref stateMachine)
AwaitOnCompleted(ref awaiter, ref stateMachine)
SetResult(...)
SetException(...)
```

Those callbacks are the only place where `MinimumPlayback` hooks into C# async
execution.  The builder turns them into runner creation, stop capture, promise
completion, and timeline records.

## Runtime Scope

`PlaybackRuntime` carries the current playback and current runner through
synchronous execution:

```text
PlaybackRuntime.CurrentPlayback
PlaybackRuntime.CurrentRunner
```

When a runner calls `MoveNext()`, it pushes itself into this ambient scope.
This is how `PlaybackTaskMethodBuilder.Create()` knows which `Playback` owns the
new async method, and how a nested async call knows its parent runner.

This works because the minimal model runs synchronously on one thread.  It is
not a general async scheduler.

## Objects

### Playback

`Playback` owns the timeline and movement cursor:

```csharp
private PlaybackRecord[] records;
private IPlaybackRunner?[] recordRunners;
private int[] stopIdsByRecord;
private int recordCount;
private int cursor;
```

`records` stores the visible timeline.  `recordRunners` maps runner-backed
boundaries, including `Call`, to a runner.  `stopIdsByRecord` maps
snapshot-backed boundaries, such as `Checkpoint` and `CallEnd`, to a
runner-local stop id.  Missing stop ids are stored as `-1`, so stop id `0` can
be used directly.

The `-1` sentinel is maintained at the small number of array resize and
truncate points.  This keeps restore and resume code readable: it can use
`stopIdsByRecord[index]` directly instead of decoding `+1` / `-1` storage
arithmetic at every access.

`Playback` also owns movement mode:

```csharp
private PlaybackMode mode;
private int rewriteFrom;
private int replayConsumeIndex;
private int replayStopIndex;
```

The modes are:

```text
Normal     normal forward movement or recording
Rewriting  next forward move truncates and rebuilds after a back step
Replaying  existing records are consumed, not appended
```

### PlaybackRunner\<TStateMachine\>

Each async method invocation gets a runner:

```csharp
internal sealed class PlaybackRunner<TStateMachine>
    where TStateMachine : IAsyncStateMachine
```

```csharp
private TStateMachine initial;
private TStateMachine current;
private TStateMachine[] stops;
private int stopCount;
```

`initial` is the state before first execution.  `current` is the executable
copy.  `stops` stores snapshots captured at explicit checkpoints and parent
await sites.  Checkpoint and call-end records point into this array.  Call
records instead restore the child runner's `initial` state.

### PlaybackPromise

`PlaybackPromise` is not a general `Task`.  It supports one logical awaiter:

```text
one child playback task -> one parent await site
```

It stores:

```csharp
private IPlaybackRunner? continuationRunner;
private int continuationId;
private Exception? exception;
private bool completed;
```

For `PlaybackTask<T>`, `PlaybackPromise<T>` also stores the result value.

The promise completion path does not invoke an arbitrary `Action`.  It resumes a
runner stop:

```text
promise complete -> continuationRunner.CompleteAwait(continuationId, childCallRecordIndex)
```

This is important for replay.  A normal `Action` would capture the runner's
mutable current state accidentally.  A stop id points to an explicit state
snapshot.

## Timeline Records

The minimal timeline record is:

```csharp
public readonly record struct PlaybackRecord(
    int Index,
    PlaybackRecordRole Role,
    string Label,
    int Depth,
    int ParentIndex
);
```

Roles:

```text
Checkpoint  explicit user checkpoint; movement stop with runner + stop id
Call        child entry boundary; movement stop with child runner initial state
CallEnd     child completion boundary; movement stop with runner + stop id
Completed   root completion boundary; movement stop without runner state
```

There is no structural-only role in the minimal model now.  `Call` has no
runner-local stop id because entering a child starts from the child runner's
initial state.  `Completed` has no runner-local stop id because the runtime
never restores from completion; it only lands on it or replays into it while
moving back.

```mermaid
flowchart TD
    Timeline["Timeline records"]
    Checkpoint["Checkpoint<br/>movement stop<br/>has runner + stop id"]
    Call["Call<br/>movement stop<br/>child runner initial"]
    CallEnd["CallEnd<br/>movement stop<br/>has runner + stop id"]
    Completed["Completed<br/>movement stop<br/>no stop id"]

    Timeline --> Checkpoint
    Timeline --> Call
    Timeline --> CallEnd
    Timeline --> Completed
```

The parent rules are:

```text
Call parent       = containing call record
Checkpoint parent = current call record
CallEnd parent    = completed child call record
```

This keeps call hierarchy separate from movement cursor state.  A call should
not be parented to the previous checkpoint just because that checkpoint was the
last movement boundary.

## Stop Storage

Runner stops are state-machine snapshots.  Checkpoint and call-end records
point to a runner stop id:

```text
record index
  -> records[index]
  -> recordRunners[index]
  -> stopIdsByRecord[index]
```

Call records point to the child runner but not to a stop id:

```text
record index
  -> records[index]
  -> child runner in recordRunners[index]
  -> child runner initial state
```

For a checkpoint:

```text
CaptureCheckpoint(ref stateMachine)
  snapshot state machine
  runner.AddStop(snapshot) -> stop id
  playback.AddCheckpoint(runner, stop id, label) -> record index
```

For an await:

```text
CaptureAwait(ref parentStateMachine)
  snapshot parent await state
  runner.AddStop(snapshot) -> stop id
  childPromise.AddContinuation(parentRunner, stop id)
```

For child completion:

```text
child Promise completes
  parentRunner.CompleteAwait(stop id, child call record index)
  playback.AddCallEnd(parentRunner, stop id, child call record index)
```

For continuing after a call end:

```text
record is CallEnd
  runner = recordRunners[record.Index]
  stopId = stopIdsByRecord[record.Index]
  runner.ResumeStop(stopId)
```

For restoring a checkpoint:

```text
record is Checkpoint
  runner = recordRunners[record.Index]
  stopId = stopIdsByRecord[record.Index]
  runner.RestoreStop(stopId, record.Index)
```

The difference is that a checkpoint restore sets `CurrentRecordIndex`, while a
call-end resume continues the parent from an await snapshot.

For root completion:

```text
root runner completes
  playback.AddCompleted() -> record index
```

Completion is a boundary, but not a resumable state.  Moving back from
completion restores the previous stop and replays forward until the completion
record is consumed.

```mermaid
sequenceDiagram
    participant Child as Child runner
    participant Promise as Child promise
    participant Parent as Parent runner
    participant Playback as Playback timeline

    Child->>Promise: SetResult(value)
    Promise->>Parent: CompleteAwait(parentStopId, childCallRecordIndex)
    Parent->>Playback: AddCallEnd(parentRunner, parentStopId, childCallRecordIndex)
    Note over Playback: Cursor can stop here before parent resumes
    Playback->>Parent: ResumeStop(parentStopId)
    Parent->>Parent: MoveNext()
```

## Forward Movement

`TryMoveNext()` is lazy.  `Playback.Create(...)` does not execute user code.
The first move starts the root async method.

The forward algorithm is:

```text
TryMoveNext
  IsForward = true
  EnsureStarted()

  if mode == Rewriting:
      truncate from rewriteFrom
      restore/resume from cursor
      run until next stop is recorded
      move cursor to that stop
      return

  next = FindNextStop(cursor)
  if next exists:
      cursor = next
      return true

  if playback is completed:
      return false

  restore/resume from cursor
  run until new stop is recorded
  cursor = next stop
```

There are two important cases for `restore/resume from cursor`:

```text
cursor == -1
  restore root initial state
  root.MoveNext()

cursor is Checkpoint
  restore runner stop snapshot
  runner.MoveNext()

cursor is Call
  restore child runner initial state
  child.MoveNext()

cursor is CallEnd
  reset runner promise for replay
  runner.ResumeStop(stop id)
```

When user code records a call, checkpoint, call-end, or completion, it appends a
new stop record in normal mode.

```mermaid
flowchart TD
    Start["TryMoveNext"]
    Ensure["EnsureStarted"]
    Rewriting{"mode == Rewriting?"}
    Truncate["Truncate timeline from rewriteFrom"]
    Existing{"Next stop already recorded?"}
    Completed{"IsCompleted?"}
    Prepare["Prepare runner from cursor"]
    Move["Move runner from cursor"]
    Find["Find next stop"]
    Cursor["cursor = next stop"]
    False["return false"]

    Start --> Ensure --> Rewriting
    Rewriting -- yes --> Truncate --> Prepare
    Rewriting -- no --> Existing
    Existing -- yes --> Cursor
    Existing -- no --> Completed
    Completed -- yes --> False
    Completed -- no --> Prepare
    Prepare --> Move --> Find --> Cursor
```

## Backward Movement

The runtime does not execute C# backward.  Backward movement means:

```text
restore the previous stop
run forward to the current stop
then move the cursor backward
```

For example, with:

```csharp
for (var i = 0; i < 5; i++)
{
    Console.Write(IsForward ? $"F{i}" : $"B{i}");
    await Checkpoint();
}
```

forward movement prints:

```text
F0 F1 F2 F3 F4
```

backward movement prints:

```text
B4 B3 B2 B1 B0
```

Each backward step replays the segment ending at the current stop while
`IsForward == false`.

The algorithm is:

```text
TryMoveBack
  EnsureStarted()
  if cursor < 0:
      return false

  target = cursor
  previous = FindPreviousStop(cursor)
  IsForward = false
  IsCompleted = false

  ReplayExisting(previous, target)

  mode = Rewriting
  rewriteFrom = previous + 1
  cursor = previous
  Current = null
```

`ReplayExisting(previous, target)` does not append records.  It consumes the
existing timeline records from `previous + 1` through `target`.

If replay does not consume exactly to `target`, the timeline and state-machine
replay have diverged and the runtime throws.

```mermaid
flowchart TD
    Start["TryMoveBack"]
    HasCursor{"cursor >= 0?"}
    Previous["previous = FindPreviousStop(cursor)"]
    Direction["IsForward = false<br/>IsCompleted = false"]
    Replay["ReplayExisting(previous, cursor)"]
    Rewrite["mode = Rewriting<br/>rewriteFrom = previous + 1"]
    MoveCursor["cursor = previous<br/>Current = null"]
    False["return false"]

    Start --> HasCursor
    HasCursor -- no --> False
    HasCursor -- yes --> Previous --> Direction --> Replay --> Rewrite --> MoveCursor
```

## Existing-Record Replay

During `PlaybackMode.Replaying`, `AddCheckpoint`, `AddCall`, `AddCallEnd`, and
root completion do not append records.  They consume the next existing record:

```text
expected index = replayConsumeIndex
expected role  = role being recorded
expected label = label being recorded
```

If role or label differs, the replayed control flow no longer matches the
existing timeline.  The minimal runtime treats that as an error.

When the consumed record reaches `replayStopIndex`, replay mode ends:

```text
mode = Normal
replayConsumeIndex = -1
replayStopIndex = -1
```

This makes backward movement deterministic: moving back from a stop proves that
the segment can be replayed forward to that same stop.

```mermaid
flowchart TD
    Existing["existing records"]
    ReplayIndex["replayConsumeIndex"]
    Operation["record operation<br/>AddCheckpoint/AddCall/AddCallEnd/Completed"]
    Match{"role + label match?"}
    Consume["consume record<br/>replayConsumeIndex++"]
    Error["throw"]
    Done{"record.Index == replayStopIndex?"}
    Normal["mode = Normal"]

    Existing --> ReplayIndex --> Operation --> Match
    Match -- no --> Error
    Match -- yes --> Consume --> Done
    Done -- yes --> Normal
```

## Rewrite After Back

After a backward step, the cursor points to the previous stop, but the timeline
still contains records after that point.  The next forward move enters
`Rewriting` mode:

```text
rewriteFrom = previous + 1
mode = Rewriting
```

Before recording anything new, the timeline is truncated from `rewriteFrom`:

```text
Array.Clear(records, rewriteFrom, recordCount - rewriteFrom)
Array.Clear(recordRunners, rewriteFrom, ...)
Array.Fill(stopIdsByRecord, -1, rewriteFrom, ...)
recordCount = rewriteFrom
mode = Normal
```

Then the runner is restored or resumed from the cursor and runs forward,
recording new records.  This is how the minimal runtime models changing future
execution after moving back.

## Nested Calls

Every nested `async PlaybackTask` call creates a child runner.

On child runner creation:

```text
parent = PlaybackRuntime.CurrentRunner
child.Depth = parent.Depth + 1
child.CallRecordIndex = playback.AddCall(parent, child, "In")
parent.AddChild(child)
```

The call record is a child-entry stop.  It gives the timeline a hierarchy and
lets movement land before the child state machine starts.

When the child finishes, its promise completes.  Since call-end is forced in the
minimal model, completion records a `CallEnd` stop instead of immediately
resuming the parent.  The next movement step from that `CallEnd` resumes the
parent await continuation.

This makes backward movement around nested calls well-defined:

```text
Call
  Checkpoint inside child
CallEnd
parent continuation checkpoint
```

Without `CallEnd`, a child completion and parent continuation can collapse into
one movement step.  That makes move-back ambiguous around await boundaries.

```mermaid
flowchart TD
    Parent["Parent runner"]
    Await["await Child()"]
    ParentStop["Capture parent await stop"]
    Call["Call stop"]
    Child["Child runner"]
    ChildCheckpoint["Child checkpoint stops"]
    ChildDone["Child SetResult"]
    CallEnd["CallEnd stop"]
    ParentResume["Resume parent await stop"]

    Parent --> Await --> ParentStop --> Call
    Call --> Child --> ChildCheckpoint --> ChildDone
    ChildDone --> CallEnd --> ParentResume
```

## Typed Return Values

`PlaybackTask<T>` uses `PlaybackPromise<T>`:

```csharp
private T? result;
```

`SetResult(T result)` completes the typed promise.  The parent continuation later
calls the awaiter `GetResult()` and receives the stored result.

The result belongs to the promise, not the awaiter or task struct.  This matters
because task structs are lightweight handles, while the promise is the stable
object shared between child completion and parent await continuation.

During replay, the promise is reset:

```text
completed = false
exception = null
```

The continuation link is preserved.  The same child completion can therefore
resume the same parent await stop again during replay.

## Debug State-Machine Cloning

In Release builds, compiler-generated async state machines are commonly value
types.  Assignment copies the state machine.

In Debug builds, state machines can be reference types.  A normal assignment
would only copy the reference, so restoring a previous checkpoint would see
mutated state.

`MinimumPlayback` uses a shallow clone helper for reference-type state machines:

```csharp
private static TStateMachine SnapshotStateMachine(TStateMachine source)
{
    if (typeof(TStateMachine).IsValueType)
        return source;

    if (source == null)
        throw new InvalidOperationException("State machine is null.");

    return CloneUtility.Clone(source);
}
```

The clone is intentionally shallow.  It is enough to copy compiler state-machine
fields themselves, but it does not deep-copy arbitrary mutable objects referenced
by locals.

## Invariants

Important invariants:

```text
Every Checkpoint record has:
  recordRunners[index] != null
  stopIdsByRecord[index] != -1

Every CallEnd record has:
  recordRunners[index] != null
  stopIdsByRecord[index] != -1

Every Completed record has:
  recordRunners[index] == null
  stopIdsByRecord[index] == -1

Call records:
  recordRunners[index] points to the child runner
  stopIdsByRecord[index] == -1
  restore the child runner initial state

During Replaying:
  each recorded operation must consume the next existing record
  role and label must match
  replay must end exactly at replayStopIndex

During Rewriting:
  records after rewriteFrom are truncated before new records are appended

PlaybackPromise:
  supports one logical awaiter
  stores a parent runner plus runner-local stop id
```

## Why There Is No General Task Semantics

`PlaybackTask` is not `Task`.

The runtime assumes:

```text
one child async method
one parent await site
one continuation stop
```

Multiple independent awaiters would imply one child completion resumes multiple
parents.  That does not fit the timeline tree.  The awaiter `OnCompleted(Action)`
methods exist only because the compiler-required awaiter shape includes them;
they throw for arbitrary use.  The supported path is through
`PlaybackTaskMethodBuilder`.

## Current Limitations

This is a deliberately small model.

Known limitations:

```text
no thread safety
no external scheduler
no ValueTask
no deep clone of mutable referenced locals
no branch mismatch recovery during replay
no public extensibility model
linear scan to find next/previous stop
```

The linear scan is over timeline records.  Since calls are now stops too, the
practical cost is usually the number of non-boundary records between movement
targets.  A stop index can be added later if that becomes a real problem.

## Mental Model

The minimal runtime can be summarized as:

```text
C# async method
  -> compiler state machine
  -> PlaybackRunner owns state-machine snapshots
  -> Playback records visible timeline events
  -> stop records map to runner-local snapshots
  -> forward movement restores/resumes and runs MoveNext
  -> backward movement restores previous stop and replays forward to current stop
  -> call-end stops make nested await completion explicit
  -> completed stops make root completion explicit
```

The timeline is not the source of truth for execution state.  It is an index
over state-machine snapshots owned by runners.
