# AsyncPlayback Core Algorithm Review

This note explains the AsyncPlayback runtime model and the proposed cleanup
direction for reviewers who do not already know the project.

## Review Focus

This review is about the runtime cleanup direction for AsyncPlayback.

The main proposal is to:

- move record-specific semantics out of `Playback` and into per-record behavior
  objects;
- move timeline lookup, boundary selection, ownership lookup, and evaluation
  candidate indexing into timeline-owned services;
- keep checkpoint restoration behind dedicated restore services rather than
  exposing runner internals to record behaviors;
- keep public custom-record extensibility provisional until real extension
  requirements are proven.

The main design decision is whether `ITimelineRecordBehavior` should become a
stable extension point now, or remain internal while the restore, evaluator,
timeline, and event boundaries settle.

## Executive Summary

AsyncPlayback makes an async workflow movable on a virtual timeline. It does not
execute C# backward. Rewind and seek work by restoring captured async state
machine snapshots, restoring playback-owned state, then replaying the relevant
segment forward while `Playback.CurrentDirection` describes the semantic
movement direction.

The useful mental model is:

```text
runner checkpoint -> timeline record -> runner checkpoint -> timeline record -> ...
```

For nested async methods, this chain exists per runner and is connected by call
records:

```text
root runner:
  checkpoint -> record -> call record -> record -> checkpoint

child runner:
                       checkpoint -> record -> checkpoint
```

The proposed direction is to model each timeline record as indexed timeline
data plus a behavior object, rather than as a record kind interpreted by
centralized switches. This keeps record semantics and record-specific runtime
state together, while allowing `Playback` to coordinate movement without knowing
every built-in record kind.

## Definitions

`PlaybackRunner<TStateMachine>` owns a compiler-generated async state machine
and its checkpoint snapshots. At an await point, the custom async method builder
receives the state machine by `ref`, copies it, and stores the copy as a
checkpoint. Restoring that checkpoint replaces the runner's current state
machine and lets `MoveNext()` resume from that await point.

`TimelineRecord` is indexed timeline storage. It has identity, start time,
duration, hierarchy fields, owner runner, optional entry checkpoint, and a
record behavior object.

`TimelineCheckpoint` connects a timeline record to the runner checkpoint needed
to enter or replay that record.

`ITimelineRecordBehavior` owns record semantics: boundaries, replay matching,
evaluation, visibility, and public info/export shape. Built-in behaviors
include delay, effect, seek loop, checkpoint, and call.

## Invariants

Every record selected for evaluation must satisfy exactly one of these cases:

1. The record has an entry checkpoint that can restore the owning runner chain
   to the point required to enter or replay the record.
2. The record is structural and is never directly evaluated.
3. The selected time is a timeline gap and is handled by gap movement logic.

A record must not be dispatched to `EvaluateAsync` unless one of these cases is
true.

Replay matching must be deterministic. A recorded operation can consume an
existing record only when the behavior says the existing record is
replay-compatible with the requested operation. Otherwise, the timeline is
truncated at the cursor before the new record is appended.

Evaluation ordering must be deterministic and direction-aware. Candidate
ordering should consider:

1. virtual time;
2. movement direction;
3. hierarchy depth;
4. record phase, such as boundary, child entry, child completion, or parent
   continuation;
5. flat index as the final stable tie-breaker.

Flat index should be the last tie-breaker, not the only semantic ordering rule.

## Recording Model

Recording is append-oriented:

1. User workflow reaches a playback awaitable or nested `PlaybackTask`.
2. Playback creates or consumes the next matching timeline record.
3. The async method builder captures the current state machine checkpoint.
4. Playback binds the checkpoint to the record.
5. The scheduler resumes continuations when the awaitable completes.

If playback is replaying an old timeline and user code records a different next
record, the timeline after the playback cursor is truncated and rebuilt. This
keeps the timeline tied to the current execution branch.

## Movement Model

Movement is target-time driven:

1. Infer direction from current time, target time, and edge state.
2. Find timeline boundaries or the record owning the target time.
3. Restore the entry checkpoint for the selected record when needed.
4. Ask the record behavior to evaluate that record at the requested time.
5. Drain scheduled continuations until the requested await point or idle state.
6. Emit public events for added records, checkpoints, and reached boundaries.

Forward movement often resumes naturally from pending awaitables. Backward
movement usually restores a checkpoint first and then replays the segment in a
controlled way.

## Backward Movement

Backward movement does not run inverse code for every statement. It moves to an
earlier virtual time by restoring a saved checkpoint and replaying the relevant
segment while `Playback.CurrentDirection` is `Backward`.

Example workflow:

```csharp
await PlaybackTask.Checkpoint("start");
await PlaybackTask.Delay(TimeSpan.FromSeconds(1), "wait");

await foreach (var frame in playback.ForEachOnSeek(TimeSpan.FromSeconds(1)))
{
    Render(frame.Progress);
}

await PlaybackTask.Checkpoint("end");
```

After recording, the timeline is conceptually:

```text
t=0.0       checkpoint "start"
t=0.0..1.0 delay "wait"
t=1.0..2.0 seek loop
t=2.0       checkpoint "end"
```

If current time is `2.0s` and the user moves to `0.7s`, playback does roughly:

```text
current = 2.0
target = 0.7
direction = Backward

while there is a boundary between current and target:
  select nearest previous boundary
  evaluate candidate records in deterministic backward order
  drain scheduled continuations

if target is owned by a record:
  evaluate owning record at target
else:
  move to timeline gap
```

For a checkpoint record:

1. Use the record's `EntryCheckpoint`.
2. Restore the runner tree to that checkpoint.
3. Restore playback-owned store state for the checkpoint.
4. Set direction and target-time context before user code resumes.
5. Run the restored state machine segment until idle or the next controlled
   await point.
6. Emit the checkpoint point boundary through the event service.

For a delay record:

1. Restore to the delay record's entry checkpoint if needed.
2. Move virtual time to the requested time inside or at the end of the delay.
3. Complete the delay promise so the state machine can continue.
4. Drain ready continuations.
5. Return internal boundary/evaluation results for the event service.

For a seek-loop record:

1. Restore to the loop record's entry checkpoint if the loop is not already
   active.
2. Compute elapsed/progress from target time.
3. Complete the loop's pending `MoveNextAsync()` with `true`.
4. Run the loop body once for that target progress.
5. Return internal boundary/evaluation results for the event service.

For an effect record:

1. Backward evaluation restores playback/store state around the effect.
2. It does not rerun the forward external effect.
3. If user code provided a separate revert effect, that revert path is invoked
   by the public `Effect(..., revert, ...)` API while replaying backward.

The state machine is not reversed. It is restored to a checkpoint and then run
forward through code while the direction flag says the semantic movement is
backward.

## Nested Runner Restore

Nested `PlaybackTask` calls add a runner chain. A child record cannot be
restored by restoring only the child runner; the parent runner must also be
restored to the await point that was waiting for the child.

For a target inside a child runner:

```text
root runner checkpoint waiting for child
  -> child runner checkpoint before target record
```

Restore works from root to leaf:

1. Build the runner chain from the target checkpoint's runner:

   ```text
   root -> child -> grandchild -> target runner
   ```

2. Restore each parent runner to the checkpoint that awaited its child.
3. Restore the target runner to the target checkpoint.
4. Reconnect parent continuations if forward replay needs the parent to resume
   when the child completes.
5. For backward replay, suppress synthetic parent continuation checkpoints so
   child completion does not create duplicate continuation records.

Parent continuation records and child entry records can share the same virtual
time. Backward stepping must choose the correct logical segment, not simply the
nearest timestamp.

## Known Edge Cases

There are two edge states:

- before the first record
- after the last record

When playback is at the terminal edge and moves backward, it may need to replay
the terminal checkpoint segment first. This handles workflows where important
state changes happen after the last timed record.

Backward targets inside a delay immediately after a seek loop also need special
handling. The target time may be inside the trailing delay, but semantically the
workflow continuation after the seek loop must be replayed backward first. This
is one reason delay and seek-loop behavior currently cooperate with playback
runtime state.

## Current Refactoring Problem

The original implementation centralized too much logic in `Playback`:

- record-kind switches
- boundary enumeration
- replay matching
- evaluation dispatch
- checkpoint restore details
- event emission
- timeline storage and truncation

Splitting the file alone is not enough. The cleanup should avoid replacing
centralized kind switches with an equally centralized O(n) polymorphic scan:

```text
for every record:
  ask behavior if it is evaluatable at time T
```

The important cleanup is to move record semantics into record behaviors and move
timeline selection/indexing into timeline-owned services.

## Proposed Ownership Boundaries

`Playback` should coordinate:

- current time, target time, direction, mode, completion
- public API entrypoints
- scheduler draining
- high-level move/run orchestration

`Timeline` should own:

- record list
- lookup by id/index
- parent/depth assignment
- add/consume/truncate/rebuild
- boundary index
- ownership index
- evaluation index
- hierarchy/call index

`TimelineNavigator` should own:

- boundary cursor
- edge state
- next boundary selection
- nearest-record lookup
- owning-record lookup for target times

`RecordEvaluator` should own:

- candidate lookup
- evaluation ordering
- behavior dispatch

Record behaviors should own:

- boundary entry declaration
- ownership/evaluation entry declaration
- replay matching
- evaluation semantics
- info/export projection

Restore/checkpoint services should own:

- restore to record
- restore to initial
- runner-chain restore
- checkpoint arming

After any restore operation:

- the root-to-leaf runner chain has been restored consistently;
- parent runners waiting on child runners are reconnected when needed;
- playback-owned store state matches the restored checkpoint;
- synthetic continuation checkpoint creation is armed or suppressed according
  to the movement mode;
- scheduler state contains only continuations valid for the restored segment;
- current direction and target-time context are set before user code resumes.

Event services should own:

- event dedupe
- internal boundary to public boundary conversion
- `PlaybackEvent` creation

Record behaviors may return internal evaluation results, but they should not
directly emit public playback events. Public event emission should happen after
evaluation through an event service, so dedupe, ordering, and conversion remain
centralized.

## Timeline Indexing Proposal

Boundary events, ownership, and evaluation dispatch are related but not the same
thing. Avoid one overloaded "evaluation index".

Recommended first indexes:

```csharp
Dictionary<TimeSpan, List<TimelineBoundaryEntry>> boundaryIndex;
List<TimelineOwnershipRange> ownershipRanges;
Dictionary<TimeSpan, List<TimelineEvaluationEntry>> pointEvaluations;
List<TimelineEvaluationRange> rangeEvaluations;
```

`boundaryIndex` answers: what logical boundaries exist at this time?

`ownershipRanges` answers: which record owns a target time inside a duration?

`pointEvaluations` answers: which zero-duration records evaluate at this exact
time?

`rangeEvaluations` answers: which behaviors evaluate continuously or
directionally over a range?

Suggested built-in entries:

```text
Delay:
  ownership range from start to end
  boundary entries at start and end
  evaluation through ownership lookup for target times inside the delay
  additional backward trailing-delay range only for seek-loop continuation handling

Effect:
  point evaluation at start time
  boundary entry at start, and end if duration becomes positive

SeekLoop:
  ownership range from start to end
  boundary entries at start and end
  range evaluation from start to end

Checkpoint:
  boundary point at start time
  backward point evaluation at start time when not implicit

Call:
  hierarchy/call index entry
  normally structural, no direct evaluation entry
```

Then evaluation becomes:

```text
owning record = timeline ownership lookup(target time)
candidate indexes = timeline evaluation index lookup(time, direction)
merge owning record when target evaluation requires it
sort small candidate set by deterministic ordering rules
behavior.EvaluateAsync(playback, record, time, direction)
```

This preserves extensibility without polling every record.

## Data Structure Recommendation

A B-tree is probably premature for the current mutation pattern. The project
appears append/truncate/rebuild oriented, not random middle insertion oriented.

Start with:

- dictionary for exact-time boundary/evaluation entries
- list for ownership ranges
- list for ranged evaluation entries
- full rebuild after truncate/reindex

If ranged records become large enough to matter, the next step is sorted lists
or an interval tree. A B-tree should only be considered after profiling shows
large timelines, frequent mutation, and range queries that simple structures
cannot handle.

## Record Behavior Abstraction

Recommendation:

Use per-record behavior objects internally now.

Keep the behavior interface internal until at least one real custom-record use
case requires public extensibility. Public custom records should not receive
direct access to runner checkpoints, scheduler internals, event emission, or
timeline mutation. They should interact through a narrow runtime context if and
when that public extension point becomes necessary.

A possible internal shape:

```csharp
internal interface ITimelineRecordBehavior
{
    void RegisterTimelineEntries(TimelineRecord record, TimelineIndexBuilder builder);
    ReplayMatchResult MatchReplay(TimelineRecord record, ReplayRequest request);
    ValueTask EvaluateAsync(TimelineRecord record, TimelineEvaluationContext context);
    TimelineRecordInfo ToInfo(TimelineRecord record);
}
```

The current experiment passes `Playback` directly to behavior evaluation. That
is acceptable while the project is still experimental, but it should not become
a public custom-record API without a narrower context.

## Restore and Event Services

The restore service should hide runner-chain mechanics from record behaviors.
The minimum future surface should look closer to:

```csharp
ValueTask RestoreToRecordEntryAsync(TimelineRecord record);
ValueTask RestoreToCheckpointAsync(TimelineCheckpoint checkpoint);
```

Record behaviors should describe what they need semantically. They should not
manipulate runner checkpoint arrays, scheduler queues, or synthetic parent
continuation state directly.

The event service should consume internal evaluation results and centralize:

- boundary dedupe
- direction-aware ordering
- public boundary kind conversion
- `PlaybackEvent` construction

## Test Scenarios

The refactor should preserve behavior for these scenarios:

1. Seek backward from after the last record into a delay.
2. Seek backward from after the last record into a seek loop.
3. Seek to before the first record.
4. Seek into a timeline gap.
5. Seek backward across a nested child `PlaybackTask`.
6. Seek across parent continuation and child entry records with the same virtual
   time.
7. Replay an existing timeline where the next record matches.
8. Replay an existing timeline where the next record mismatches and truncates.
9. Evaluate an effect backward without rerunning the forward external effect.
10. Evaluate an effect backward with a public revert effect.
11. Seek into a delay immediately after a seek loop.
12. Rebuild indexes after truncation and verify candidate lookup remains
    deterministic.

## Review Questions

The main design questions for review are:

- Is per-record behavior the right internal abstraction, or should built-ins
  remain internal services with no behavior interface?
- Should `ITimelineRecordBehavior` stay internal until custom-record
  requirements are concrete?
- Which behavior operations, if any, need to become public?
- Should timeline evaluation entries be behavior-defined but timeline-owned?
- Should boundary, ownership, and evaluation indexes stay separate?
- What is the minimal restore operation surface record behaviors should call?
- Which playback internals are accidental coupling and should move into
  restore, evaluator, timeline, or event services?

## Recommended Answers

Per-record behavior is a good internal abstraction. It removes record-kind
switches from `Playback` and keeps typed per-record state, such as
`EffectRecordBehavior<T>`, close to the behavior that uses it.

Built-ins should remain internal services/behaviors for now. Promote the
behavior interface to public only after custom-record requirements are concrete.

Public behavior operations should probably be declarative first: boundary
registration, ownership/evaluation entry registration, replay matching,
info/export projection, and evaluation through a narrow context. Do not expose
runner checkpoints or scheduler internals.

Timeline evaluation entries should be behavior-defined but timeline-owned.
Behaviors should declare what entries they need. Timeline should own storage,
invalidation, sorting, rebuilding, and query performance.

Most likely accidental couplings are boundary enumeration, replay matching,
restore-chain construction, synthetic continuation checkpoint suppression, event
dedupe, public event conversion, truncation/reindex logic, and candidate
selection.
