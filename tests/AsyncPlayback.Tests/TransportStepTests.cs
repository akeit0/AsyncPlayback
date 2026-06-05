using System.Globalization;
using AsyncPlayback;
using Microsoft.Extensions.Time.Testing;

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
    public async Task CurrentDirection_IsBackwardWhileReplayingTerminalEdge()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => TerminalEdgeDirectionScenario(r, directions));

        await playback.RunToEndAsync();
        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Forward]);

        directions.Clear();
        await StepBackAsync(playback);

        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Backward]);
    }

    [Test]
    public async Task MoveToAsync_IsNoOpAtSameTimeFromTerminalEdge()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => TerminalEdgeDirectionScenario(r, directions));

        await playback.RunToEndAsync();
        directions.Clear();

        await playback.MoveToAsync(TimeSpan.Zero);

        await Assert.That(directions).IsEmpty();
    }

    [Test]
    public async Task CurrentDirection_IsForwardWhileReplayingInitialEdge()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => InitialEdgeDirectionScenario(r, directions));

        await playback.RunToEndAsync();
        directions.Clear();

        await playback.RunBackToStartAsync();
        directions.Clear();

        await StepForwardAsync(playback);

        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Forward]);
    }

    [Test]
    public async Task CurrentDirection_IsBackwardWhileReplayingInitialEdge()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => InitialEdgeDirectionScenario(r, directions));

        await playback.RunToEndAsync();
        directions.Clear();

        await playback.RunBackToStartAsync();

        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Backward]);
    }

    [Test]
    public async Task MoveToAsync_IsNoOpAtSameTimeFromInitialEdge()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => InitialEdgeDirectionScenario(r, directions));

        await playback.RunToEndAsync();
        directions.Clear();

        await playback.RunBackToStartAsync();
        directions.Clear();

        await playback.MoveToAsync(TimeSpan.Zero);

        await Assert.That(directions).IsEmpty();
    }

    [Test]
    public async Task MoveToAsync_CanUseExplicitDirectionForSameTimeEdge()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => TerminalEdgeDirectionScenario(r, directions));

        await playback.RunToEndAsync();
        directions.Clear();

        await playback.MoveToAsync(TimeSpan.Zero, PlaybackDirection.Backward);

        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Backward]);
    }

    [Test]
    public async Task MoveToAsync_CanUseExplicitForwardDirectionForSameTimeInitialEdge()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => InitialEdgeDirectionScenario(r, directions));

        await playback.RunToEndAsync();
        directions.Clear();

        await playback.RunBackToStartAsync();
        directions.Clear();

        await playback.MoveToAsync(TimeSpan.Zero, PlaybackDirection.Forward);

        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Forward]);
    }

    [Test]
    public async Task MoveToAsync_RejectsExplicitForwardDirectionForEarlierTarget()
    {
        var playback = Playback.Start(r => DelayScenario(r, []));

        await playback.RunToEndAsync();

        await Assert
            .That(async () =>
                await playback.MoveToAsync(
                    TimeSpan.FromMilliseconds(500),
                    PlaybackDirection.Forward
                )
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task MoveToAsync_RejectsExplicitBackwardDirectionForLaterTarget()
    {
        var playback = Playback.Start(r => DelayScenario(r, []));

        await Assert
            .That(async () =>
                await playback.MoveToAsync(
                    TimeSpan.FromMilliseconds(500),
                    PlaybackDirection.Backward
                )
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task MoveToAsync_ReplaysTerminalEdgeAfterSeekLoopWhenMovingBackwardFromEnd()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => SeekLoopTerminalEdgeScenario(r, directions));

        await playback.RunToEndAsync();
        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Forward]);
        directions.Clear();

        await playback.MoveToAsync(TimeSpan.Zero);

        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Backward]);
    }

    [Test]
    public async Task MoveToAsync_ReplaysTerminalEdgeAfterSeekLoopWhenMovingBackwardIntoLoop()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => SeekLoopTerminalEdgeScenario(r, directions));

        await playback.RunToEndAsync();
        directions.Clear();

        await playback.MoveToAsync(TimeSpan.FromSeconds(0.5));

        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Backward]);
    }

    [Test]
    public async Task MoveToAsync_ReplaysTerminalEdgeAfterRealtimeSeekLoopCompletion()
    {
        var timeProvider = new FakeTimeProvider();
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(
            r => SeekLoopTerminalEdgeScenario(r, directions),
            timeProvider
        );

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await playback.AdvanceByElapsedTimeAsync();
        await playback.RunToEndAsync();
        directions.Clear();

        await playback.MoveToAsync(TimeSpan.FromSeconds(0.5));

        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Backward]);
    }

    [Test]
    public async Task MoveToAsync_ReplaysTerminalEdgeAfterRealtimeSeekLoopOvershootsEnd()
    {
        var timeProvider = new FakeTimeProvider();
        var events = new List<string>();
        var playback = Playback.Start(r => UiLikeSeekLoopScenario(r, events), timeProvider);

        timeProvider.Advance(TimeSpan.FromSeconds(1.05));
        await playback.AdvanceByElapsedTimeAsync();
        await Assert.That(Joined(events)).IsEqualTo("Forwardstart,Forwardmiddle,Forwardend");
        await Assert.That(playback.Time).IsEqualTo(TimeSpan.FromSeconds(1));
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromSeconds(0.5));

        await Assert.That(Joined(events)).IsEqualTo("Backwardend");
    }

    [Test]
    public async Task TryStepForwardAsync_DoesNotReplaySeekLoopTerminalEdgeAfterRealtimeCompletion()
    {
        var timeProvider = new FakeTimeProvider();
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(
            r => SeekLoopTerminalEdgeScenario(r, directions),
            timeProvider
        );

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await playback.AdvanceByElapsedTimeAsync();
        directions.Clear();

        var result = await playback.TryStepForwardAsync();

        await Assert.That(result.Moved).IsFalse();
        await Assert.That(directions).IsEmpty();
    }

    [Test]
    public async Task MoveToAsync_PastEndPreservesSeekLoopTerminalEdgeForBackwardMove()
    {
        var directions = new List<PlaybackDirection>();
        var playback = Playback.Start(r => SeekLoopTerminalEdgeScenario(r, directions));

        await playback.RunToEndAsync();
        directions.Clear();

        await playback.MoveToAsync(TimeSpan.FromSeconds(1.05));
        await playback.MoveToAsync(TimeSpan.FromSeconds(0.5));

        await Assert.That(directions).IsEquivalentTo([PlaybackDirection.Backward]);
    }

    [Test]
    public async Task MoveToAsync_RepeatedSameTimeAtStartDoesNotReplayInitialEdge()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => UiLikeSeekLoopScenario(r, events));

        await playback.RunToEndAsync();
        events.Clear();

        await playback.MoveToAsync(TimeSpan.Zero);
        await playback.MoveToAsync(TimeSpan.Zero);

        await Assert.That(Joined(events)).IsEqualTo("Backwardend,Backwardmiddle,Backwardstart");
    }

    [Test]
    public async Task MoveToAsync_ForwardFromStartReplaysSameTimeCheckpointContinuations()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => UiLikeSeekLoopScenario(r, events));

        await playback.RunToEndAsync();
        events.Clear();

        await playback.MoveToAsync(TimeSpan.Zero);
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromSeconds(1));

        await Assert.That(Joined(events)).IsEqualTo("Forwardstart,Forwardmiddle,Forwardend");
    }

    [Test]
    public async Task SelectByDirection_RestoresPreviousValuesAtTerminalAndInitialSegments()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => SelectByDirectionScenario(r, events));

        await playback.RunToEndAsync();
        await Assert.That(Joined(events)).IsEqualTo("simulate start: Working...,simulate end: End");
        events.Clear();

        await playback.MoveToAsync(TimeSpan.Zero);
        await Assert
            .That(Joined(events))
            .IsEqualTo("simulate end: Working...,simulate start: Hello, world!");
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromSeconds(1));
        await Assert.That(Joined(events)).IsEqualTo("simulate start: Working...,simulate end: End");
    }

    [Test]
    public async Task SelectByDirection_DoesNotBreakBackwardSeekLoopBody()
    {
        var events = new List<string>();
        var panel = new TestPanel();
        var playback = Playback.Start(r => SelectAndSeekLoopScenario(r, events, panel));

        await playback.RunToEndAsync();
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1650));

        await Assert.That(Joined(events)).IsEqualTo("end:Working...,rect:0.5");
        await Assert.That(panel.Rects.Count).IsEqualTo(1);
        await Assert.That(panel.Rects[0].Width).IsEqualTo(225);
    }

    [Test]
    public async Task SelectByDirection_RepeatedBackwardTerminalReplayDoesNotOverwriteStoredValue()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointSelectSeekLoopScenario(r, events));

        await playback.RunToEndAsync();
        await Assert.That(Joined(events)).IsEqualTo("selected:Done!");
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1500));
        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1200));
        await playback.MoveToAsync(TimeSpan.FromMilliseconds(900));

        await Assert.That(Joined(events)).IsEqualTo("selected:Running");
    }

    [Test]
    public async Task SelectByDirection_BackwardScrubNearEndDoesNotReplayForwardTerminalRepeatedly()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointSelectSeekLoopScenario(r, events));

        await playback.RunToEndAsync();
        events.Clear();

        foreach (var seconds in new[] { 1.99, 1.98, 1.995, 1.97, 1.96, 1.95 })
            await playback.MoveToAsync(TimeSpan.FromSeconds(seconds));

        await Assert.That(Joined(events)).IsEqualTo("selected:Running");
    }

    [Test]
    public async Task SelectByDirection_ForwardSamplesInsideSeekLoopDoNotReplayTerminal()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointSelectSeekLoopScenario(r, events));

        await playback.RunToEndAsync();
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1500));
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1600));
        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1700));
        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1800));
        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1900));

        await Assert.That(Joined(events)).IsEmpty();
    }

    [Test]
    public async Task SelectByDirection_RepeatedForwardTargetsAtEndDoNotReplayTerminal()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointSelectSeekLoopScenario(r, events));

        await playback.RunToEndAsync();
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1500));
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromSeconds(2));
        await playback.MoveToAsync(TimeSpan.FromSeconds(2));
        await playback.MoveToAsync(TimeSpan.FromSeconds(2));

        await Assert.That(Joined(events)).IsEqualTo("selected:Done!");
    }

    [Test]
    public async Task SelectByDirection_RepeatedFullRewindCyclesReplayOncePerDirection()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointSelectSeekLoopScenario(r, events));

        await playback.RunToEndAsync();
        var recordCount = playback.Records.Count;
        events.Clear();

        for (var i = 0; i < 5; i++)
        {
            await playback.MoveToAsync(TimeSpan.Zero);
            await Assert.That(playback.Records.Count).IsEqualTo(recordCount);
            await playback.MoveToAsync(TimeSpan.FromSeconds(2));
            await Assert.That(playback.Records.Count).IsEqualTo(recordCount);
        }

        await Assert
            .That(Joined(events))
            .IsEqualTo(
                "selected:Running,selected:Done!,selected:Running,selected:Done!,selected:Running,selected:Done!,selected:Running,selected:Done!,selected:Running,selected:Done!"
            );
    }

    [Test]
    public async Task SelectByDirection_TinyCrossingsAroundSeekLoopEndReplayBothDirections()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointSelectSeekLoopScenario(r, events));

        await playback.RunToEndAsync();
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1999));
        await playback.MoveToAsync(TimeSpan.FromSeconds(2));
        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1998));
        await playback.MoveToAsync(TimeSpan.FromSeconds(2));

        await Assert
            .That(Joined(events))
            .IsEqualTo("selected:Running,selected:Done!,selected:Running,selected:Done!");
    }

    [Test]
    public async Task SelectByDirection_DoesNotOverwriteBackwardValueWithForwardExternalState()
    {
        var events = new List<string>();
        var label = new TestLabel { Text = "Hello, world!" };
        var poisonForwardReplay = false;
        var playback = Playback.Start(r =>
            ExternalLabelSelectSeekLoopScenario(r, events, label, () => poisonForwardReplay)
        );

        await playback.RunToEndAsync();
        events.Clear();

        await playback.MoveToAsync(TimeSpan.Zero);
        events.Clear();

        poisonForwardReplay = true;
        await playback.MoveToAsync(TimeSpan.FromSeconds(2));
        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1990));

        await Assert
            .That(Joined(events))
            .IsEqualTo("selected:Done!:Forward,selected:Running:Backward");
    }

    [Test]
    public async Task SelectByDirection_RemainsStableAfterRepeatedFullReplays()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => CheckpointSelectSeekLoopScenario(r, events));

        await playback.RunToEndAsync();
        await playback.MoveToAsync(TimeSpan.Zero);
        await playback.MoveToAsync(TimeSpan.FromSeconds(2));
        await playback.MoveToAsync(TimeSpan.Zero);
        await playback.MoveToAsync(TimeSpan.FromSeconds(2));
        events.Clear();

        foreach (var seconds in new[] { 1.99, 1.8, 1.6, 1.4 })
            await playback.MoveToAsync(TimeSpan.FromSeconds(seconds));

        await Assert.That(Joined(events)).IsEqualTo("selected:Running");
    }

    [Test]
    public async Task SelectByDirection_DoesNotBreakRepeatedBackwardSeekLoopSamples()
    {
        var events = new List<string>();
        var panel = new TestPanel();
        var playback = Playback.Start(r => SelectAndSeekLoopScenario(r, events, panel));

        await playback.RunToEndAsync();
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromMilliseconds(2700));
        await playback.MoveToAsync(TimeSpan.FromMilliseconds(2100));
        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1500));

        await Assert.That(panel.Rects.Count).IsEqualTo(1);
        await Assert.That(panel.Rects[0].Width).IsEqualTo(250);
        await Assert.That(Joined(events)).Contains("rect:0.889");
        await Assert.That(Joined(events)).Contains("rect:0.667");
        await Assert.That(Joined(events)).Contains("rect:0.444");
    }

    [Test]
    public async Task SeekLoopBackwardUsesCurrentRunStateAfterReplayingForwardAgain()
    {
        var events = new List<string>();
        var panel = new TestPanel();
        var playback = Playback.Start(r => SelectAndSeekLoopScenario(r, events, panel));

        await playback.RunToEndAsync();
        await playback.MoveToAsync(TimeSpan.Zero);
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromSeconds(3));
        await Assert.That(panel.Rects.Count).IsEqualTo(1);
        var secondRunRect = panel.Rects[0];
        events.Clear();

        await playback.MoveToAsync(TimeSpan.FromMilliseconds(1650));

        await Assert.That(panel.Rects.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(panel.Rects[0], secondRunRect)).IsTrue();
        await Assert.That(secondRunRect.Width).IsEqualTo(225);
        await Assert.That(Joined(events)).IsEqualTo("end:Working...,rect:0.5");
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
        var timeProvider = new FakeTimeProvider();
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
        var timeProvider = new FakeTimeProvider();
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
        var timeProvider = new FakeTimeProvider();
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
        var timeProvider = new FakeTimeProvider();
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
    public async Task EffectAsync_UsesProviderElapsedTimeAsTimelineDuration()
    {
        var timeProvider = new FakeTimeProvider();
        var events = new List<string>();
        var playback = Playback.Start(
            r => TimedEffectScenario(r, events, timeProvider),
            timeProvider
        );

        await playback.RunToEndAsync();

        var effect = playback.Records.Single(static record =>
            record is { Kind: TimelineRecordKind.Effect, DebugLabel: "timed effect" }
        );

        await Assert.That(effect.StartTime).IsEqualTo(TimeSpan.Zero);
        await Assert.That(effect.Duration).IsEqualTo(TimeSpan.FromMilliseconds(300));
        await Assert.That(effect.EndTime).IsEqualTo(TimeSpan.FromMilliseconds(300));
        await Assert.That(playback.Time).IsEqualTo(TimeSpan.FromMilliseconds(300));
        await Assert.That(Joined(events)).IsEqualTo("effect,after");
    }

    [Test]
    public async Task Records_MarkEntryAndImplicitCheckpointsAsInfrastructure()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => TypedHelperScenario(r, events));

        await playback.RunToEndAsync();

        await Assert
            .That(
                playback
                    .Records.Where(static record =>
                        record.DebugLabel.StartsWith("entry ", StringComparison.Ordinal)
                        || record.DebugLabel.StartsWith("after ", StringComparison.Ordinal)
                    )
                    .All(static record =>
                        record.Visibility == TimelineRecordVisibility.Infrastructure
                    )
            )
            .IsTrue();
        await Assert
            .That(playback.Records.Single(static record => record.DebugLabel == "outer").Visibility)
            .IsEqualTo(TimelineRecordVisibility.Workflow);
    }

    [Test]
    public async Task ExportTimeline_IncludesRecordSpansAndPeriodicSamples()
    {
        var timeProvider = new FakeTimeProvider();
        var events = new List<string>();
        var playback = Playback.Start(
            r => TimedEffectScenario(r, events, timeProvider),
            timeProvider
        );

        await playback.RunToEndAsync();

        var export = playback.ExportTimeline(
            new TimelineExportOptions { SampleInterval = TimeSpan.FromMilliseconds(100) }
        );
        var effect = export.Records.Single(static record => record.Label == "timed effect");

        await Assert.That(export.Schema).IsEqualTo("async-playback.timeline.v1");
        await Assert.That(effect.DurationSeconds).IsEqualTo(0.3);
        await Assert.That(effect.Visibility).IsEqualTo("Workflow");
        await Assert
            .That(export.Samples.Any(sample => sample.ActiveRecordIds.Contains(effect.Id)))
            .IsTrue();
        await Assert.That(playback.ExportTimelineJson()).Contains("\"visibility\"");
        await Assert.That(playback.ExportTimelineJson()).Contains("\"schema\"");
    }

    [Test]
    public async Task Records_UseReadablePlaybackTaskMethodNames()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => TypedHelperScenario(r, events));

        await playback.RunToEndAsync();

        await Assert
            .That(playback.Records.Select(static record => record.DebugLabel))
            .Contains("entry TypedHelperScenario");
        await Assert
            .That(playback.Records.Select(static record => record.DebugLabel))
            .Contains("TypedHelper");
        await Assert
            .That(playback.Records.Any(static record => record.DebugLabel.Contains('<')))
            .IsFalse();
    }

    [Test]
    public async Task Records_AfterAwaitRemainInCallerScope()
    {
        var timeProvider = new FakeTimeProvider();
        var events = new List<string>();
        var playback = Playback.Start(
            r => EffectThenChildCallScenario(r, events, timeProvider),
            timeProvider
        );

        await playback.RunToEndAsync();

        var effect = playback.Records.Single(static record => record.DebugLabel == "outer effect");
        var childCall = playback.Records.Single(static record =>
            record.DebugLabel == "NestedDelay"
        );
        var delay = playback.Records.Single(static record => record.DebugLabel == "nested delay");

        await Assert.That(childCall.ParentId).IsNotEqualTo(effect.Id);
        await Assert.That(childCall.Depth).IsEqualTo(0);
        await Assert.That(delay.ParentId).IsEqualTo(childCall.Id);
        await Assert.That(delay.Depth).IsEqualTo(1);
    }

    [Test]
    public async Task MoveByAsync_BackwardAcrossCallStart_DoesNotEnterChildTwice()
    {
        var events = new List<string>();
        var playback = Playback.Start(r => NestedSeekLoopScenario(r, events));

        await playback.MoveToAsync(TimeSpan.FromSeconds(2));
        events.Clear();

        for (var i = 0; i < 4; i++)
            await playback.MoveByAsync(-TimeSpan.FromSeconds(0.4));

        await Assert.That(events.Count(static value => value == "child:start")).IsEqualTo(1);
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
        await Assert.That(restoredStates).IsEquivalentTo([2]);

        await StepBackAsync(playback);
        await Assert.That(restoredStates).IsEquivalentTo([2, 1]);
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
            await PlaybackTask.Checkpoint();
        }
    }

    private static async PlaybackTask SeekLoopScenario(Playback playback, List<string> events)
    {
        await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(1)))
            events.Add("seek:" + progress.Progress.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static async PlaybackTask NestedSeekLoopScenario(Playback playback, List<string> events)
    {
        events.Add("root:start");

        await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(0.5)))
            events.Add(
                "root:a:" + progress.Progress.ToString("0.###", CultureInfo.InvariantCulture)
            );

        await NestedSeekLoopChild(playback, events);

        events.Add("root:after-child");

        await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(0.5)))
            events.Add(
                "root:b:" + progress.Progress.ToString("0.###", CultureInfo.InvariantCulture)
            );

        events.Add("root:end");
    }

    private static async PlaybackTask NestedSeekLoopChild(Playback playback, List<string> events)
    {
        events.Add("child:start");

        await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(1)))
            events.Add(
                "child:loop:" + progress.Progress.ToString("0.###", CultureInfo.InvariantCulture)
            );

        events.Add("child:end");
    }

    private static async PlaybackTask DelayScenario(Playback playback, List<string> events)
    {
        events.Add("delay:start");
        await PlaybackTask.Delay(TimeSpan.FromSeconds(1));
        events.Add("delay:end");
    }

    private static async PlaybackTask StoreScenario(Playback playback)
    {
        playback.Store("start");
        await PlaybackTask.Delay(TimeSpan.FromSeconds(1));
        playback.Store("end");
        await PlaybackTask.Checkpoint();
    }

    private static async PlaybackTask ClearStoreScenario(Playback playback)
    {
        playback.Store("start");
        await PlaybackTask.Checkpoint("stored");

        playback.ClearStore();
        await PlaybackTask.Checkpoint("cleared");
    }

    private static async PlaybackTask CheckpointStoreScenario(Playback playback)
    {
        for (var i = 0; i < 3; i++)
        {
            playback.Store(i);
            await PlaybackTask.Checkpoint();
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
            await PlaybackTask.Checkpoint();
        }
    }

    private static async PlaybackTask TerminalEdgeDirectionScenario(
        Playback playback,
        List<PlaybackDirection> directions
    )
    {
        await PlaybackTask.Checkpoint("edge");
        directions.Add(playback.CurrentDirection);
    }

    private static async PlaybackTask InitialEdgeDirectionScenario(
        Playback playback,
        List<PlaybackDirection> directions
    )
    {
        directions.Add(playback.CurrentDirection);
        await PlaybackTask.Checkpoint("edge");
    }

    private static async PlaybackTask SeekLoopTerminalEdgeScenario(
        Playback playback,
        List<PlaybackDirection> directions
    )
    {
        await foreach (var _ in playback.ForEachOnSeek(TimeSpan.FromSeconds(1))) { }

        directions.Add(playback.CurrentDirection);
    }

    private static async PlaybackTask UiLikeSeekLoopScenario(Playback playback, List<string> events)
    {
        events.Add(playback.CurrentDirection + "start");
        await PlaybackTask.Checkpoint("start work");
        events.Add(playback.CurrentDirection + "middle");
        await PlaybackTask.Checkpoint("rectangle added");

        await foreach (var _ in playback.ForEachOnSeek(TimeSpan.FromSeconds(1))) { }

        events.Add(playback.CurrentDirection + "end");
    }

    private static async PlaybackTask SelectByDirectionScenario(
        Playback playback,
        List<string> events
    )
    {
        var text = "Hello, world!";

        text = SelectText(playback, events, "start", text, "Working...");
        await PlaybackTask.Delay(TimeSpan.FromMilliseconds(300));
        await PlaybackTask.Checkpoint("rectangle added");

        await foreach (var _ in playback.ForEachOnSeek(TimeSpan.FromMilliseconds(700))) { }

        text = SelectText(playback, events, "end", text, "End");
    }

    private static string SelectText(
        Playback playback,
        List<string> events,
        string label,
        string previousText,
        string newText
    )
    {
        var selected = playback.SelectByDirection(backwardStore: previousText, forward: newText);
        events.Add($"simulate {label}: {selected}");
        return selected;
    }

    private static async PlaybackTask SelectAndSeekLoopScenario(
        Playback playback,
        List<string> events,
        TestPanel panel
    )
    {
        var text = "Hello, world!";

        text = playback.SelectByDirection(backwardStore: text, forward: "Working...");
        await PlaybackTask.Delay(TimeSpan.FromMilliseconds(300));
        var rect = AddTestRect(playback, events, panel);
        await PlaybackTask.Checkpoint("rectangle added");

        await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromMilliseconds(2700)))
        {
            rect.Width = 450 * (1 - progress.Progress);
            events.Add("rect:" + progress.Progress.ToString("0.###", CultureInfo.InvariantCulture));
        }

        text = playback.SelectByDirection(backwardStore: text, forward: "End");
        events.Add("end:" + text);
    }

    private static async PlaybackTask CheckpointSelectSeekLoopScenario(
        Playback playback,
        List<string> events
    )
    {
        var text = "Hello, world!";

        await PlaybackTask.Checkpoint();
        text = playback.CurrentDirection == PlaybackDirection.Forward ? "Running" : text;
        await PlaybackTask.Delay(TimeSpan.FromSeconds(1));

        await foreach (var _ in playback.ForEachOnSeek(TimeSpan.FromSeconds(1))) { }

        text = playback.SelectByDirection(backwardStore: text, forward: "Done!");
        events.Add("selected:" + text);
    }

    private static async PlaybackTask ExternalLabelSelectSeekLoopScenario(
        Playback playback,
        List<string> events,
        TestLabel label,
        Func<bool>? poisonForwardBeforeSelect = null
    )
    {
        var initialText = label.Text;

        await PlaybackTask.Checkpoint();
        label.Text =
            playback.CurrentDirection == PlaybackDirection.Forward ? "Running" : initialText;
        await PlaybackTask.Delay(TimeSpan.FromSeconds(1));

        await foreach (var _ in playback.ForEachOnSeek(TimeSpan.FromSeconds(1))) { }

        if (
            playback.CurrentDirection == PlaybackDirection.Forward
            && poisonForwardBeforeSelect?.Invoke() == true
        )
            label.Text = "Done!";

        var selected = playback.SelectByDirection(backwardStore: label.Text, forward: "Done!");
        events.Add($"selected:{selected}:{playback.CurrentDirection}");
        label.Text = selected;
    }

    private static TestRect AddTestRect(Playback playback, List<string> events, TestPanel panel)
    {
        if (playback.CurrentDirection == PlaybackDirection.Forward)
        {
            events.Add("add");
            var rect = new TestRect();
            panel.Rects.Add(rect);
            return rect;
        }

        events.Add("remove");
        var last = panel.Rects[^1];
        panel.Rects.RemoveAt(panel.Rects.Count - 1);
        return last;
    }

    private sealed class TestPanel
    {
        public List<TestRect> Rects { get; } = [];
    }

    private sealed class TestLabel
    {
        public string Text { get; set; } = "";
    }

    private sealed class TestRect
    {
        public double Width { get; set; }
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

            await PlaybackTask.Checkpoint();
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

        await PlaybackTask.Checkpoint("decision");
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
            await PlaybackTask.Checkpoint("a");
            events.Add("a");
        }
        else if (value == 1)
        {
            await PlaybackTask.Checkpoint("b");
            events.Add("b");
        }
        else
        {
            await PlaybackTask.Checkpoint("c");
            events.Add("c");
        }

        await PlaybackTask.Checkpoint("after");
    }

    private static async PlaybackTask LogicalStepScenario(Playback playback)
    {
        await LogicalStepHelper(playback);
        await PlaybackTask.Checkpoint("outer");
    }

    private static async PlaybackTask LogicalStepHelper(Playback playback)
    {
        await PlaybackTask.Checkpoint("inner");
    }

    private static async PlaybackTask TypedHelperScenario(Playback playback, List<string> events)
    {
        var value = await TypedHelper(playback, events);
        playback.Store(value);
        events.Add("outer:" + value.ToString(CultureInfo.InvariantCulture));
        await PlaybackTask.Checkpoint("outer");
    }

    private static async PlaybackTask<int> TypedHelper(Playback playback, List<string> events)
    {
        await PlaybackTask.Checkpoint("typed helper");
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
        await PlaybackTask.Checkpoint();
    }

    private static async PlaybackTask TimedEffectScenario(
        Playback playback,
        List<string> events,
        FakeTimeProvider timeProvider
    )
    {
        await playback.Effect(
            () =>
            {
                events.Add("effect");
                timeProvider.Advance(TimeSpan.FromMilliseconds(300));
                return ValueTask.CompletedTask;
            },
            "timed effect"
        );

        events.Add("after");
        await PlaybackTask.Checkpoint("after timed effect");
    }

    private static async PlaybackTask EffectThenChildCallScenario(
        Playback playback,
        List<string> events,
        FakeTimeProvider timeProvider
    )
    {
        await playback.Effect(
            () =>
            {
                events.Add("effect");
                timeProvider.Advance(TimeSpan.FromMilliseconds(100));
                return ValueTask.CompletedTask;
            },
            "outer effect"
        );

        await NestedDelay(playback);
    }

    private static async PlaybackTask NestedDelay(Playback playback)
    {
        await PlaybackTask.Delay(TimeSpan.FromMilliseconds(100), "nested delay");
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

        await PlaybackTask.Checkpoint("state");

        if (playback.CurrentDirection == PlaybackDirection.Forward)
            events.Add("after");

        await PlaybackTask.Checkpoint("after");
    }

    private static async PlaybackTask BackwardEffectScenario(Playback playback, List<string> events)
    {
        if (playback.CurrentDirection == PlaybackDirection.Forward)
            playback.Store("forward");

        await PlaybackTask.Checkpoint("state");

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

        await PlaybackTask.Checkpoint("after");
    }

    private static async PlaybackTask BackwardNestedEffectScenario(
        Playback playback,
        List<string> events
    )
    {
        if (playback.CurrentDirection == PlaybackDirection.Forward)
            playback.Store("forward");

        await PlaybackTask.Checkpoint("state");

        if (
            playback.CurrentDirection == PlaybackDirection.Backward
            && playback.TryGet<string>(out var state)
        )
        {
            await RestoreStateAsync(playback, events, state);
        }

        await PlaybackTask.Checkpoint("after");
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

        await PlaybackTask.Checkpoint();
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

        await PlaybackTask.Checkpoint();
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
}
