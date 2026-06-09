# MinimumPlayback Internal Algorithm

This document explains how `src/MinimumPlayback` works internally. 
The goal is to describe the algorithm: how C# async state machines
are captured, recorded as timeline boundaries, moved forward, and replayed to
move backward.

`MinimumPlayback` is deliberately small. It has checkpoints, nested
`PlaybackTask` calls, typed return values, call-entry and call-exit boundaries,
root completion, and backward movement. It has no virtual time, scheduler,
`ValueTask`, general `Task` semantics, or user data store.

## 1. Observable Behavior

Consider this program:

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

Output:

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

Backward movement is not C# running in reverse. A backward step restores an
earlier state-machine snapshot, runs the generated `MoveNext()` forward until
the current timeline boundary is reproduced, consumes that boundary, and then
moves the cursor backward.

```text
forward  = restore current boundary  -> run MoveNext -> land on next boundary
backward = restore previous boundary -> run MoveNext -> consume current boundary
```

## 2. Core Model

C# lowers an `async` method into an `IAsyncStateMachine`. That generated state
machine owns the resume state, locals, awaiter fields, and a `MoveNext()` method.

`MinimumPlayback` uses the generated state machine as executable state. It does
not interpret user code. It stores snapshots of the state machine at known
movement boundaries.

The main runtime objects are:

```text
Playback
  timeline records
  record -> runner map
  record -> runner-local stop id map
  movement cursor
  replay/rewrite mode

PlaybackRunner<TStateMachine>
  initial state-machine snapshot
  current executable snapshot
  stops[] snapshots captured at checkpoints and parent await sites

PlaybackPromise
  child completion state
  parent continuation runner
  parent continuation stop id
```

The timeline is not executable state. It is an index over visible movement
boundaries:

```text
record index -> PlaybackRecord metadata
record index -> runner, for runner-backed boundaries
record index -> stop id, for snapshot-backed boundaries
```

```mermaid
flowchart LR
    RecordIndex["record index"]
    Record["records[index]<br/>role, label, depth, parent"]
    RunnerMap["recordRunners[index]<br/>runner-backed boundaries"]
    StopMap["stopIdsByRecord[index]<br/>snapshot-backed boundaries"]
    RunnerInitial["runner.initial<br/>Call"]
    RunnerStop["runner.stops[stopId]<br/>Checkpoint / CallEnd"]

    RecordIndex --> Record
    RecordIndex --> RunnerMap
    RecordIndex --> StopMap
    RunnerMap --> RunnerInitial
    RunnerMap --> RunnerStop
    StopMap --> RunnerStop
```

`Call` is runner-backed but not stop-id-backed: it starts the child runner from
that runner's `initial` snapshot. `Checkpoint` and `CallEnd` are backed by a
runner plus a runner-local stop id. `Completed` is a boundary with no runner
state.

## 3. Nested Call and the Call Boundary

`Call` is easiest to understand from a nested example:

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

Forward output:

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

Backward output from completion:

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

This dump helper prints the resulting timeline:

```csharp
static void DumpRecord(PlaybackRecord record)
{
    Console.WriteLine(
        $"{record.Index, 3}: {string.Concat(Enumerable.Repeat("| ", record.Depth))}{record.Label} depth={record.Depth} parent={record.ParentIndex}"
    );
}

static void Dump(Playback playback)
{
    for (var i = 0; i < playback.Records.Length; i++)
    {
        DumpRecord(playback.Records[i]);
    }
}
```

Timeline:

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

The `Call` record lets movement stop before a child state machine starts. Moving
forward from `Call` restores the child runner's `initial` state and calls
`MoveNext()`. The `CallEnd` record lets movement stop after the child has
completed but before the parent await continuation resumes.

```mermaid
flowchart TD
    Parent["Parent runner"]
    Await["await child"]
    ParentStop["capture parent await stop"]
    Call["Call stop<br/>child not started"]
    Child["child initial -> MoveNext"]
    ChildStops["child checkpoints / nested calls"]
    CallEnd["CallEnd stop<br/>child completed"]
    ParentResume["resume parent await stop"]

    Parent --> Await --> ParentStop --> Call
    Call --> Child --> ChildStops --> CallEnd --> ParentResume
```

## 4. Generated State Machine Example

The sample `ForScenario` is lowered into one generated state machine. Exact
names differ by compiler, but the shape is what matters:

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

    void IAsyncStateMachine.MoveNext() => MoveNext();

    void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)
    {
        builder.SetStateMachine(stateMachine);
    }
}
```

The fields that matter in this example are:

| Field | Meaning |
| --- | --- |
| `state` | Compiler resume point. |
| `i` | User loop local. |
| `checkpointAwaiter` | Awaiter contract for the suspended operation. `GetResult()` is the resume point after `await`: it propagates exceptions and returns the awaited value. `Checkpoint()` returns void, but typed child awaits use the same pattern to return `T`. |
| `builder` | Custom builder that lets `MinimumPlayback` capture and resume execution. |

For this example, the meaningful state is mostly `(state, i)`:

| Boundary | `state` | `i` |
| --- | ---: | --- |
| `initial` | `-1` | undefined |
| `stop0` | `0` | `0` |
| `stop1` | `0` | `1` |
| `stop2` | `0` | `2` |
| `Completed` | `-2` | `3` |

Direction changes timeline bookkeeping, not generated `MoveNext()` execution.

```mermaid
flowchart TD
    I["initial<br/>state=-1"]
    S0["stop0<br/>state=0, i=0"]
    S1["stop1<br/>state=0, i=1"]
    S2["stop2<br/>state=0, i=2"]
    C["Completed<br/>state=-2, i=3"]

    I -->|"MoveNext entry<br/>write 0"| S0
    S0 -->|"MoveNext resume<br/>i++ ; write 1"| S1
    S1 -->|"MoveNext resume<br/>i++ ; write 2"| S2
    S2 -->|"MoveNext resume<br/>i++ ; write done"| C

    S0 -. "back: replay initial -> stop0<br/>cursor becomes before first" .-> I
    S1 -. "back: replay stop0 -> stop1<br/>cursor becomes stop0" .-> S0
    S2 -. "back: replay stop1 -> stop2<br/>cursor becomes stop1" .-> S1
    C -. "back: replay stop2 -> Completed<br/>cursor becomes stop2" .-> S2
```

## 5. Builder and Runner Lifecycle

`PlaybackTask` and `PlaybackTask<T>` use custom async method builders:

```csharp
[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder))]
public readonly partial struct PlaybackTask;

[AsyncMethodBuilder(typeof(PlaybackTaskMethodBuilder<>))]
public readonly struct PlaybackTask<T>;
```

The compiler-generated state machine calls the custom builder instead of
`AsyncTaskMethodBuilder`. The real dispatch lives in
`PlaybackTaskMethodBuilderCore`.

### Start

`Start(ref stateMachine)` creates a runner:

```text
parent = PlaybackRuntime.CurrentRunner
runner = new PlaybackRunner<TStateMachine>(playback, promise, parent)
promise.AttachRunner(runner)
runner.SetInitial(ref stateMachine)
```

For the root async method:

```text
playback.AttachRootRunner(runner)
runner.MoveNext()
```

The root starts immediately because the first `TryMoveNext()` must run user code
until the first boundary.

For a nested `PlaybackTask` call:

```text
playback.AddCall(parent, childRunner, "In")
```

The child does not start immediately. The `Call` record is the movement boundary
before the child starts.

### CaptureAwait

`CaptureAwait(ref awaiter, ref stateMachine)` handles incomplete awaits:

```text
awaiter.IsReplaySuspension:
  CaptureReplaySuspension(ref stateMachine, awaiter.ReplayOwnerRecordIndex)

awaiter.CheckpointLabel is label:
  CaptureCheckpoint(ref stateMachine, label)

awaiter.Promise is childPromise:
  parentStopId = CaptureAwait(ref stateMachine)
  childPromise.AddContinuation(parentRunner, parentStopId)
```

Checkpoint capture snapshots the current state machine, stores it in
`runner.stops[]`, and records `Checkpoint`. Child-await capture snapshots the
parent await site and registers that stop id on the child promise.

### Complete

`Complete()` and `Complete<T>(...)` mark the runner as completed and complete its
promise.

For the root runner, `MarkCompleted()` records `Completed`. For a child runner,
promise completion records `CallEnd` through the registered parent continuation:

```text
child promise completes
parentRunner.CompleteAwait(parentStopId, childCallRecordIndex)
playback.AddCallEnd(parentRunner, parentStopId, childCallRecordIndex)
```

`SetException(...)` follows the same completion path but stores the exception in
the promise.

`PlaybackRuntime` provides the ambient synchronous scope:

```text
CurrentPlayback
CurrentRunner
```

When a runner calls `MoveNext()`, it pushes itself as `CurrentRunner`. This is
how a nested async method discovers its parent runner.

## 6. Timeline Records and Stop Storage

Timeline records are small value records:

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
Checkpoint  explicit user checkpoint; runner + stop id
Call        child entry boundary; child runner initial state
CallEnd     child completion boundary; parent runner + stop id
Completed   root completion boundary; no runner state
```

All four roles are movement boundaries.

Storage rules:

```text
Checkpoint:
  recordRunners[index] = owning runner
  stopIdsByRecord[index] = runner-local stop id

Call:
  recordRunners[index] = child runner
  stopIdsByRecord[index] = -1

CallEnd:
  recordRunners[index] = parent runner
  stopIdsByRecord[index] = parent await stop id

Completed:
  recordRunners[index] = null
  stopIdsByRecord[index] = -1
```

`stopIdsByRecord` stores the real stop id. Missing stop ids are `-1`, defined in
code as `NoStopId`. The `-1` sentinel is maintained where the array is resized
or truncated, so restore/resume code can use `stopIdsByRecord[index]` directly.

Parent/depth rules:

```text
Call parent       = containing call record, or -1 at root
Checkpoint parent = current call record, or -1 at root
CallEnd parent    = completed child call record
Completed parent  = -1
```

## 7. Moving Forward

`Playback.Create(...)` is lazy. It stores the entry delegate but does not run
user code. The first `TryMoveNext()` starts the root runner and runs until the
first boundary is recorded.

If the next stop already exists, moving forward only advances the cursor:

```text
next = FindNextStop(cursor)
cursor = next
Current = records[next]
```

If there is no recorded next stop and playback is not completed, the runtime
restores or resumes from the current cursor and executes `MoveNext()` until a
new record is appended.

Cursor preparation:

```text
cursor == -1:
  restore root initial
  root.MoveNext()

cursor is Checkpoint:
  restore runner.stops[stopId]
  runner.MoveNext()

cursor is Call:
  restore child runner initial
  child.MoveNext()

cursor is CallEnd:
  reset parent promise for replay
  resume parent await stop

cursor is Completed:
  no forward segment exists
```

If the previous backward move put playback in `Rewriting` mode, the next forward
move first truncates records from `rewriteFrom`, fills cleared stop ids with
`-1`, and then records a new future.

## 8. Moving Back

`TryMoveBack()` never executes C# backward. It replays a forward segment while
`IsForward == false`.

Algorithm:

```text
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

`ReplayExisting(previous, target)` sets replay mode, restores the runner for
`previous`, runs forward, and consumes existing records from `previous + 1`
through `target`.

During replay, record creation methods do not append:

```text
AddCheckpoint
AddCall
AddCallEnd
OnRootCompleted
```

They consume the next existing record instead. Role and label must match. If
replay does not consume exactly through `target`, the runtime throws because the
state-machine execution no longer matches the timeline.

After a successful backward step, the cursor points to `previous`, but the old
future still exists in arrays. `Rewriting` mode means the next forward movement
will truncate that future before recording anything new.

## 9. Typed Results, Exceptions, and Cloning

`PlaybackTask<T>` stores its result in `PlaybackPromise<T>`, not in the task
struct. The task struct is only a handle; the promise is the shared object
between child completion and parent continuation.

During replay, a promise resets:

```text
completed = false
exception = null
```

The continuation runner and stop id remain linked, so the same replayed child
completion can record the same `CallEnd` and later resume the same parent await
state.

Exceptions are stored in the promise and thrown from `GetResult()`.

State-machine snapshots are ordinary assignment for value-type state machines.
In Debug builds, generated state machines can be reference types, so
`MinimumPlayback` uses a shallow `MemberwiseClone()` helper. The clone copies
state-machine fields, but it does not deep-copy mutable objects referenced by
locals.

## 10. Constraints and Mental Model

Constraints:

```text
single-threaded synchronous runtime
no external scheduler
no ValueTask
no general Task semantics
one logical awaiter per PlaybackTask
shallow clone only for reference-type state machines
linear scan for next/previous stop
```

Mental model:

| Step | Meaning |
| ---: | --- |
| 1 | C# async method becomes a compiler state machine. |
| 2 | `PlaybackRunner` snapshots that state machine. |
| 3 | `Playback` records visible movement boundaries. |
| 4 | `TryMoveNext` restores or resumes and runs `MoveNext()`. |
| 5 | `TryMoveBack` restores the previous boundary and replays forward. |
| 6 | `Rewriting` truncates the old future after a backward step. |
