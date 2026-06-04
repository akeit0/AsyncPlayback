using System.Globalization;
using AsyncPlayback;

namespace AsyncPlayback.Tests;

public sealed class TransportStepTests
{
    [Test]
    public async Task RunToEndAsync_ReplaysCheckpointSegmentsOnce()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointScenario(r, events));

        await playback.RunToEndAsync();

        await Assert.That(Joined(events)).IsEqualTo("0,1,2");
    }

    [Test]
    public async Task TryStepForwardAsync_StopsAtExplicitCheckpoints()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointScenario(r, events));

        var first = await StepForwardAsync(playback);
        await Assert.That(first.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Joined(events)).IsEqualTo("0");

        var second = await StepForwardAsync(playback);
        await Assert.That(second.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Joined(events)).IsEqualTo("0,1");

        var third = await StepForwardAsync(playback);
        await Assert.That(third.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Joined(events)).IsEqualTo("0,1,2");

        await AssertStepForwardFalseAsync(playback);
        await Assert.That(Joined(events)).IsEqualTo("0,1,2");
    }

    [Test]
    public async Task TryStepBackAsync_ReplaysCheckpointSegmentsWithoutTerminalNoOp()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointScenario(r, events));
        await playback.RunToEndAsync();
        events.Clear();

        var first = await StepBackAsync(playback);
        await Assert.That(first.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Joined(events)).IsEqualTo("2");

        var second = await StepBackAsync(playback);
        await Assert.That(second.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Joined(events)).IsEqualTo("2,1");

        var third = await StepBackAsync(playback);
        await Assert.That(third.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Joined(events)).IsEqualTo("2,1,0");

        await AssertStepBackFalseAsync(playback);
        await Assert.That(Joined(events)).IsEqualTo("2,1,0");
    }

    [Test]
    public async Task CurrentDirection_IsBackwardWhileReplayingCheckpointBack()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => DirectionScenario(r, directions));
        await playback.RunToEndAsync();
        directions.Clear();

        await StepBackAsync(playback);
        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Backward]);
    }

    [Test]
    public async Task TryStepForwardAsync_StopsAtSeekLoopStartAndEnd()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => SeekLoopScenario(r, events));

        var start = await StepForwardAsync(playback);
        await Assert.That(start.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Joined(events)).IsEqualTo("seek:0");

        var end = await StepForwardAsync(playback);
        await Assert.That(end.Time).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(Joined(events)).IsEqualTo("seek:0,seek:1");

        await AssertStepForwardFalseAsync(playback);
    }

    [Test]
    public async Task TryStepBackAsync_StopsAtSeekLoopEndAndStart()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => SeekLoopScenario(r, events));
        await playback.RunToEndAsync();
        events.Clear();

        var end = await StepBackAsync(playback);
        await Assert.That(end.Time).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(Joined(events)).IsEqualTo("seek:1");

        var start = await StepBackAsync(playback);
        await Assert.That(start.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Joined(events)).IsEqualTo("seek:1,seek:0");

        await AssertStepBackFalseAsync(playback);
    }

    [Test]
    public async Task TryStepForwardAsync_StopsAtDelayStartAndEnd()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => DelayScenario(r, events));

        var start = await StepForwardAsync(playback);
        await Assert.That(start.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Joined(events)).IsEqualTo("delay:start");

        var end = await StepForwardAsync(playback);
        await Assert.That(end.Time).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(Joined(events)).IsEqualTo("delay:start,delay:end");

        await AssertStepForwardFalseAsync(playback);
    }

    [Test]
    public async Task MoveByAsync_Traverse_EvaluatesSeekLoopIntermediateTargets()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => SeekLoopScenario(r, events));

        await playback.MoveByAsync(TimeSpan.FromSeconds(0.25));
        await playback.MoveByAsync(TimeSpan.FromSeconds(0.25));
        await playback.MoveByAsync(TimeSpan.FromSeconds(0.5));

        await Assert.That(Joined(events)).IsEqualTo("seek:0.25,seek:0.5,seek:1");
    }

    [Test]
    public async Task TryStepForwardAsync_ReportsDeltaTimeFromTimeProvider()
    {
        var timeProvider = new ManualTimeProvider();
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointScenario(r, events), timeProvider);

        timeProvider.Advance(TimeSpan.FromMilliseconds(250));

        var result = await StepForwardAsync(playback);

        await Assert.That(result.DeltaTime).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(playback.DeltaTime).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(result.Record?.DeltaTime).IsEqualTo(TimeSpan.FromMilliseconds(250));
    }

    [Test]
    public async Task TryStepForwardAsync_UpdatesTimestampAtReachedAwaitPoint()
    {
        var timeProvider = new ManualTimeProvider();
        var playback = Playback.Start(r => CheckpointScenario(r, []), timeProvider);

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        var firstTimestamp = timeProvider.GetTimestamp();

        await StepForwardAsync(playback);

        await Assert.That(playback.Timestamp).IsEqualTo(firstTimestamp);

        timeProvider.Advance(TimeSpan.FromMilliseconds(75));
        var secondTimestamp = timeProvider.GetTimestamp();

        var result = await StepForwardAsync(playback);

        await Assert.That(playback.Timestamp).IsEqualTo(secondTimestamp);
        await Assert.That(result.Timestamp).IsEqualTo(secondTimestamp);
        await Assert.That(result.Record?.Timestamp).IsEqualTo(secondTimestamp);
        await Assert.That(playback.DeltaTime).IsEqualTo(TimeSpan.FromMilliseconds(75));
    }

    [Test]
    public async Task AdvanceByElapsedTimeAsync_MovesByTimeProviderDelta()
    {
        var timeProvider = new ManualTimeProvider();
        var events = new List<string>();
        var playback = Playback.Start(r => SeekLoopScenario(r, events), timeProvider);

        timeProvider.Advance(TimeSpan.FromMilliseconds(250));

        await playback.AdvanceByElapsedTimeAsync();

        await Assert.That(playback.Time).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(playback.DeltaTime).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(Joined(events)).IsEqualTo("seek:0.25");
    }

    [Test]
    public async Task RewindByElapsedTimeAsync_MovesBackByTimeProviderDelta()
    {
        var timeProvider = new ManualTimeProvider();
        var events = new List<string>();
        var playback = Playback.Start(r => SeekLoopScenario(r, events), timeProvider);

        await playback.MoveToAsync(TimeSpan.FromSeconds(1));
        events.Clear();

        timeProvider.Advance(TimeSpan.FromMilliseconds(400));

        await playback.RewindByElapsedTimeAsync();

        await Assert.That(playback.Time).IsEqualTo(TimeSpan.FromMilliseconds(600));
        await Assert.That(playback.DeltaTime).IsEqualTo(TimeSpan.FromMilliseconds(400));
        await Assert.That(Joined(events)).IsEqualTo("seek:0.6");
    }

    [Test]
    public async Task Store_RestoresObjectByTimelinePosition()
    {
        var playback = Playback.Start(StoreScenario);

        await playback.MoveToAsync(TimeSpan.FromSeconds(0.5));
        await Assert.That(playback.TryGet<string>(out var middleState)).IsTrue();
        await Assert.That(middleState).IsEqualTo("start");

        await playback.MoveToAsync(TimeSpan.FromSeconds(1));
        await Assert.That(playback.TryGet<string>(out var endState)).IsTrue();
        await Assert.That(endState).IsEqualTo("end");

        await playback.MoveToAsync(TimeSpan.FromSeconds(0.5));
        await Assert.That(playback.TryGet<string>(out var restoredState)).IsTrue();
        await Assert.That(restoredState).IsEqualTo("start");
    }

    [Test]
    public async Task Store_IsSingleCurrentObject_NotTypeKeyed()
    {
        var playback = Playback.Start(StoreScenario);

        await playback.RunToEndAsync();

        await Assert.That(playback.TryGet<int>(out _)).IsFalse();
        await Assert.That(playback.TryGet<string>(out var state)).IsTrue();
        await Assert.That(state).IsEqualTo("end");
    }

    [Test]
    public async Task ClearStore_RemovesCurrentObjectAndRestoresByCheckpointPosition()
    {
        var playback = Playback.Start(ClearStoreScenario);

        await playback.RunToEndAsync();
        await Assert.That(playback.TryGet<string>(out _)).IsFalse();

        await StepBackAsync(playback);
        await Assert.That(playback.TryGet<string>(out _)).IsFalse();

        await StepBackAsync(playback);
        await Assert.That(playback.TryGet<string>(out var restored)).IsTrue();
        await Assert.That(restored).IsEqualTo("start");
    }

    [Test]
    public async Task TryStepBackAsync_RestoresStoredObjectAtCheckpointPosition()
    {
        var playback = Playback.Start(CheckpointStoreScenario);

        await playback.RunToEndAsync();

        await Assert.That(playback.TryGet<int>(out var endState)).IsTrue();
        await Assert.That(endState).IsEqualTo(2);

        await StepBackAsync(playback);
        await Assert.That(playback.TryGet<int>(out var secondState)).IsTrue();
        await Assert.That(secondState).IsEqualTo(2);

        await StepBackAsync(playback);
        await Assert.That(playback.TryGet<int>(out var firstState)).IsTrue();
        await Assert.That(firstState).IsEqualTo(1);

        await StepBackAsync(playback);
        await Assert.That(playback.TryGet<int>(out var initialState)).IsTrue();
        await Assert.That(initialState).IsEqualTo(0);

        await AssertStepBackFalseAsync(playback);
    }

    [Test]
    public async Task TryStepBackAsync_RestoresStoredObjectAtDelayBoundaries()
    {
        var playback = Playback.Start(StoreScenario);

        await playback.RunToEndAsync();

        await Assert.That(playback.TryGet<string>(out var endState)).IsTrue();
        await Assert.That(endState).IsEqualTo("end");

        var delayEnd = await StepBackAsync(playback);
        await Assert.That(delayEnd.Time).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(playback.TryGet<string>(out var delayEndState)).IsTrue();
        await Assert.That(delayEndState).IsEqualTo("end");

        var delayStart = await StepBackAsync(playback);
        await Assert.That(delayStart.Time).IsEqualTo(TimeSpan.Zero);
        await Assert.That(playback.TryGet<string>(out var delayStartState)).IsTrue();
        await Assert.That(delayStartState).IsEqualTo("start");

        await AssertStepBackFalseAsync(playback);
    }

    [Test]
    public async Task CurrentDirection_IsForwardWhileMovingNext()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => DirectionScenario(r, directions));

        await StepForwardAsync(playback);
        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Forward]);
    }

    [Test]
    public async Task Scenario_CanStoreOnForwardAndReadOnBackward()
    {
        var restoredStates = new List<int>();
        var playback = Playback.Start(r => DirectionControlledStoreScenario(r, restoredStates));

        await playback.RunToEndAsync();

        await Assert.That(restoredStates.Count).IsEqualTo(0);

        await StepBackAsync(playback);
        await Assert.That(restoredStates).IsEquivalentTo([1]);

        await StepBackAsync(playback);
        await Assert.That(restoredStates).IsEquivalentTo([1, 0]);
    }

    [Test]
    public async Task Scenario_CanSelectDifferentCheckpointsFromRestoredStateAcrossDirections()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => DirectionBranchScenario(r, events));

        await StepForwardAsync(playback);
        await Assert.That(JoinedUserRecordLabels(playback)).IsEqualTo("decision");

        await StepForwardAsync(playback);
        await Assert.That(JoinedUserRecordLabels(playback)).IsEqualTo("decision,a");
        await Assert.That(Joined(events)).IsEqualTo("");

        await StepForwardAsync(playback);
        await Assert.That(JoinedUserRecordLabels(playback)).IsEqualTo("decision,a,after");
        await Assert.That(Joined(events)).IsEqualTo("a");

        events.Clear();

        await StepBackAsync(playback);
        await Assert.That(JoinedUserRecordLabels(playback)).IsEqualTo("decision,a,after");
        await Assert.That(Joined(events)).IsEqualTo("a");

        events.Clear();

        await StepBackAsync(playback);
        await Assert.That(JoinedUserRecordLabels(playback)).IsEqualTo("decision,b");
        await Assert.That(Joined(events)).IsEqualTo("");

        await StepForwardAsync(playback);
        await Assert.That(JoinedUserRecordLabels(playback)).IsEqualTo("decision,b,after");
        await Assert.That(Joined(events)).IsEqualTo("b");

        events.Clear();

        await StepBackAsync(playback);
        await Assert.That(JoinedUserRecordLabels(playback)).IsEqualTo("decision,b,after");
        await Assert.That(Joined(events)).IsEqualTo("b");

        events.Clear();

        await StepBackAsync(playback);
        await Assert.That(JoinedUserRecordLabels(playback)).IsEqualTo("decision,c");
        await Assert.That(Joined(events)).IsEqualTo("");

        await StepForwardAsync(playback);
        await Assert.That(JoinedUserRecordLabels(playback)).IsEqualTo("decision,c,after");
        await Assert.That(Joined(events)).IsEqualTo("c");
    }

    [Test]
    public async Task TryStepForwardAsync_LogicalGranularityReportsUserDebugLabelsOnly()
    {
        var playback = Playback.Start(LogicalStepScenario);
        var labels = new List<string>();

        while ((await playback.TryStepForwardAsync(PlaybackStepGranularity.Logical)).Moved)
            labels.Add(playback.CurrentRecord?.DebugLabel ?? "<none>");

        await Assert.That(Joined(labels)).IsEqualTo("inner,outer");
    }

    [Test]
    public async Task StepResult_ExposesBoundaryDebugInfo()
    {
        var playback = Playback.Start(LogicalStepScenario);

        var result = await playback.TryStepForwardAsync(PlaybackStepGranularity.Logical);

        await Assert.That(result.Moved).IsTrue();
        await Assert.That(result.DebugLabel).IsEqualTo("inner");
        await Assert.That(result.Record?.Kind).IsEqualTo(TimelineRecordKind.Checkpoint);
        await Assert.That(result.BoundaryKind).IsEqualTo(PlaybackBoundaryKind.Point);
    }

    [Test]
    public async Task GetNearestDebugLabels_ReturnsRecordsNearCurrentTime()
    {
        var playback = Playback.Start(LogicalStepScenario);

        await playback.RunToEndAsync();

        await Assert.That(playback.GetNearestDebugLabels()).Contains("outer");
    }

    [Test]
    public async Task AsyncPlaybackTaskOfT_CanReturnValueFromHelperMethod()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => TypedHelperScenario(r, events));

        await playback.RunToEndAsync();

        await Assert.That(Joined(events)).IsEqualTo("helper,outer:7");
        await Assert.That(playback.TryGet<int>(out var value)).IsTrue();
        await Assert.That(value).IsEqualTo(7);
    }

    [Test]
    public async Task EffectAsync_RunsForwardAndUserStoresResult()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => EffectStoreScenario(r, events));

        await playback.RunToEndAsync();

        await Assert.That(Joined(events)).IsEqualTo("effect,stored");
        await Assert.That(playback.TryGet<int>(out var value)).IsTrue();
        await Assert.That(value).IsEqualTo(42);
    }

    [Test]
    public async Task EffectAsync_DoesNotReplayForwardEffectWhenMovingBackwardAcrossIt()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => ForwardOnlyEffectScenario(r, events));

        await playback.RunToEndAsync();
        await Assert.That(Joined(events)).IsEqualTo("effect,after");

        events.Clear();

        await playback.RunBackToStartAsync();

        await Assert.That(Joined(events)).IsEqualTo("");
    }

    [Test]
    public async Task EffectAsync_RunsUserRestoreLogicOnBackward()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => BackwardEffectScenario(r, events));

        await playback.RunToEndAsync();
        events.Clear();

        await StepBackAsync(playback);

        await Assert.That(Joined(events)).IsEqualTo("restore:forward");
    }

    [Test]
    public async Task EffectAsync_RunsNestedUserRestoreLogicOnBackward()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => BackwardNestedEffectScenario(r, events));

        await playback.RunToEndAsync();
        events.Clear();

        await StepBackAsync(playback);

        await Assert.That(Joined(events)).IsEqualTo("restore:forward");
    }

    [Test]
    public async Task EffectAsync_ExceptionFlowsThroughAwait()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => EffectExceptionScenario(r, events));

        await playback.RunToEndAsync();

        await Assert.That(Joined(events)).IsEqualTo("caught");
    }

    [Test]
    public async Task EffectAsync_ReceivesTransportCancellationToken()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => EffectCancellationScenario(r, events));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await StepForwardAsync(playback, cancellation.Token);
        await StepForwardAsync(playback);

        await Assert.That(Joined(events)).IsEqualTo("cancelled");
    }

    private static async Task<StepResult> StepForwardAsync(
        Playback playback,
        CancellationToken cancellationToken = default
    )
    {
        var result = await playback.TryStepForwardAsync(cancellationToken);
        await Assert.That(result.Moved).IsTrue();
        return result;
    }

    private static async Task<StepResult> StepBackAsync(Playback playback)
    {
        var result = await playback.TryStepBackAsync();
        await Assert.That(result.Moved).IsTrue();
        return result;
    }

    private static async Task AssertStepForwardFalseAsync(Playback playback)
    {
        var result = await playback.TryStepForwardAsync();
        await Assert.That(result.Moved).IsFalse();
    }

    private static async Task AssertStepBackFalseAsync(Playback playback)
    {
        var result = await playback.TryStepBackAsync();
        await Assert.That(result.Moved).IsFalse();
    }

    private static async PlaybackTask CheckpointScenario(Playback playback, List<string> events)
    {
        for (var i = 0; i < 3; i++)
        {
            events.Add(i.ToString(CultureInfo.InvariantCulture));
            await playback.Checkpoint();
        }
    }

    private static async PlaybackTask SeekLoopScenario(Playback playback, List<string> events)
    {
        await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(1)))
            events.Add("seek:" + progress.Progress.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static async PlaybackTask DelayScenario(Playback playback, List<string> events)
    {
        events.Add("delay:start");
        await playback.Delay(TimeSpan.FromSeconds(1));
        events.Add("delay:end");
    }

    private static async PlaybackTask StoreScenario(Playback playback)
    {
        playback.Store("start");
        await playback.Delay(TimeSpan.FromSeconds(1));
        playback.Store("end");
        await playback.Checkpoint();
    }

    private static async PlaybackTask ClearStoreScenario(Playback playback)
    {
        playback.Store("start");
        await playback.Checkpoint("stored");

        playback.ClearStore();
        await playback.Checkpoint("cleared");
    }

    private static async PlaybackTask CheckpointStoreScenario(Playback playback)
    {
        for (var i = 0; i < 3; i++)
        {
            playback.Store(i);
            await playback.Checkpoint();
        }
    }

    private static async PlaybackTask DirectionScenario(
        Playback playback,
        List<PlaybackDirection> directions
    )
    {
        for (var i = 0; i < 2; i++)
        {
            directions.Add(playback.CurrentDirection);
            await playback.Checkpoint();
        }
    }

    private static async PlaybackTask DirectionControlledStoreScenario(
        Playback playback,
        List<int> restoredStates
    )
    {
        for (var i = 0; i < 3; i++)
        {
            if (playback.CurrentDirection == PlaybackDirection.Forward)
            {
                playback.Store(i);
            }
            else if (playback.TryGet<int>(out var state))
            {
                restoredStates.Add(state);
            }

            await playback.Checkpoint();
        }
    }

    private static async PlaybackTask DirectionBranchScenario(
        Playback playback,
        List<string> events
    )
    {
        if (playback.CurrentDirection == PlaybackDirection.Forward)
        {
            playback.Store(0);
        }

        await playback.Checkpoint("decision");
        var value = 0;
        if (
            playback.CurrentDirection == PlaybackDirection.Backward
            && playback.TryGet<int>(out value)
        )
        {
            value++;
            playback.Store(value);
        }

        if (value == 0)
        {
            await playback.Checkpoint("a");
            events.Add("a");
        }
        else if (value == 1)
        {
            await playback.Checkpoint("b");
            events.Add("b");
        }
        else
        {
            await playback.Checkpoint("c");
            events.Add("c");
        }

        await playback.Checkpoint("after");
    }

    private static async PlaybackTask LogicalStepScenario(Playback playback)
    {
        await LogicalStepHelper(playback);
        await playback.Checkpoint("outer");
    }

    private static async PlaybackTask LogicalStepHelper(Playback playback)
    {
        await playback.Checkpoint("inner");
    }

    private static async PlaybackTask TypedHelperScenario(Playback playback, List<string> events)
    {
        var value = await TypedHelper(playback, events);
        playback.Store(value);
        events.Add("outer:" + value.ToString(CultureInfo.InvariantCulture));
        await playback.Checkpoint("outer");
    }

    private static async PlaybackTask<int> TypedHelper(Playback playback, List<string> events)
    {
        await playback.Checkpoint("typed helper");
        events.Add("helper");
        return 7;
    }

    private static async PlaybackTask EffectStoreScenario(Playback playback, List<string> events)
    {
        var value = await playback.Effect(
            () =>
            {
                events.Add("effect");
                return ValueTask.FromResult(42);
            },
            "load"
        );

        playback.Store(value);
        events.Add("stored");
        await playback.Checkpoint();
    }

    private static async PlaybackTask ForwardOnlyEffectScenario(
        Playback playback,
        List<string> events
    )
    {
        if (playback.CurrentDirection == PlaybackDirection.Forward)
        {
            await playback.Effect(
                () =>
                {
                    events.Add("effect");
                    return ValueTask.CompletedTask;
                },
                "forward effect"
            );
        }

        await playback.Checkpoint("state");

        if (playback.CurrentDirection == PlaybackDirection.Forward)
            events.Add("after");

        await playback.Checkpoint("after");
    }

    private static async PlaybackTask BackwardEffectScenario(Playback playback, List<string> events)
    {
        if (playback.CurrentDirection == PlaybackDirection.Forward)
            playback.Store("forward");

        await playback.Checkpoint("state");

        if (
            playback.CurrentDirection == PlaybackDirection.Backward
            && playback.TryGet<string>(out var state)
        )
        {
            await playback.Effect(
                async () =>
                {
                    await Task.Delay(10);
                    events.Add("restore:" + state);
                },
                "restore"
            );
        }

        await playback.Checkpoint("after");
    }

    private static async PlaybackTask BackwardNestedEffectScenario(
        Playback playback,
        List<string> events
    )
    {
        if (playback.CurrentDirection == PlaybackDirection.Forward)
            playback.Store("forward");

        await playback.Checkpoint("state");

        if (
            playback.CurrentDirection == PlaybackDirection.Backward
            && playback.TryGet<string>(out var state)
        )
        {
            await RestoreStateAsync(playback, events, state);
        }

        await playback.Checkpoint("after");
    }

    private static async PlaybackTask RestoreStateAsync(
        Playback playback,
        List<string> events,
        string state
    )
    {
        await playback.Effect(
            async () =>
            {
                await Task.Delay(10);
                events.Add("restore:" + state);
            },
            "restore"
        );
    }

    private static async PlaybackTask EffectExceptionScenario(
        Playback playback,
        List<string> events
    )
    {
        try
        {
            await playback.Effect<int>(
                () => ValueTask.FromException<int>(new InvalidOperationException("boom")),
                "failing"
            );
        }
        catch (InvalidOperationException)
        {
            events.Add("caught");
        }

        await playback.Checkpoint();
    }

    private static async PlaybackTask EffectCancellationScenario(
        Playback playback,
        List<string> events
    )
    {
        try
        {
            await playback.Effect(
                async cancellationToken =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    events.Add("completed");
                },
                "cancel"
            );
        }
        catch (OperationCanceledException)
        {
            events.Add("cancelled");
        }

        await playback.Checkpoint();
    }

    private static string Joined(List<string> values)
    {
        return string.Join(",", values);
    }

    private static string JoinedUserRecordLabels(Playback playback)
    {
        return string.Join(
            ",",
            playback
                .Records.Select(static record => record.DebugLabel)
                .Where(static label => !label.StartsWith("entry ", StringComparison.Ordinal))
        );
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return timestamp;
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(elapsed));

            timestamp += elapsed.Ticks;
        }
    }
}
