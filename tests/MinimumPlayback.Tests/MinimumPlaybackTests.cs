using MinimumPlayback;
using static MinimumPlayback.PlaybackTask;

namespace MinimumPlayback.Tests;

public sealed class MinimumPlaybackTests
{
    [Test]
    public async Task Create_DoesNotRunUntilFirstMoveNext()
    {
        var events = new List<string>();
        var playback = Playback.Create(_ => Scenario(events));

        await Assert.That(events).IsEmpty();

        await Assert.That(playback.TryMoveNext()).IsTrue();
        await Assert.That(Joined(events)).IsEqualTo("0");

        static async PlaybackTask Scenario(List<string> events)
        {
            events.Add("0");
            await Checkpoint("first");
        }
    }

    [Test]
    public async Task MoveBack_ReplaysCheckpointSegmentInBackwardDirection()
    {
        var events = new List<string>();
        var playback = Playback.Create(_ => Scenario(events));

        while (playback.TryMoveNext()) { }
        await Assert.That(Joined(events)).IsEqualTo("F0,F1,F2,F3,F4");

        events.Clear();
        while (playback.TryMoveBack()) { }
        await Assert.That(Joined(events)).IsEqualTo("B4,B3,B2,B1,B0");

        events.Clear();
        while (playback.TryMoveNext()) { }
        await Assert.That(Joined(events)).IsEqualTo("F0,F1,F2,F3,F4");

        static async PlaybackTask Scenario(List<string> events)
        {
            for (var i = 0; i < 5; i++)
            {
                events.Add(IsForward ? $"F{i}" : $"B{i}");
                await Checkpoint(i.ToString());
            }
        }
    }

    [Test]
    public async Task NestedTypedTask_ReplaysAfterRepeatedBackAndNext()
    {
        var playback1 = Playback.Create(_ => Scenario(6));
        var playback2 = Playback.Create(_ => Scenario(4));

        for (var i = 0; i < 128 && (!playback1.IsCompleted || !playback2.IsCompleted); i++)
        {
            var playback = (i & 1) == 0 ? playback1 : playback2;
            if (playback.IsCompleted)
                continue;

            if (i % 3 == 2)
                playback.TryMoveBack();
            else
                playback.TryMoveNext();
        }

        await Assert.That(Labels(playback2)).Contains("end=3");
    }

    [Test]
    public async Task InterleavedPlaybacks_AdvanceIndependently()
    {
        var events = new List<string>();
        var playback1 = Playback.Create(_ => LinearScenario("A", events));
        var playback2 = Playback.Create(_ => LinearScenario("B", events));

        await Assert.That(playback1.TryMoveNext()).IsTrue();
        await Assert.That(playback2.TryMoveNext()).IsTrue();
        await Assert.That(playback1.TryMoveNext()).IsTrue();
        await Assert.That(playback2.TryMoveNext()).IsTrue();

        await Assert.That(Joined(events)).IsEqualTo("A0,B0,A1,B1");
    }

    private static async PlaybackTask Scenario(int n)
    {
        await Checkpoint("start");
        var value = await Fib(n);
        await Checkpoint($"end={value}");
    }

    private static async PlaybackTask<int> Fib(int n)
    {
        if (n <= 1)
            return n;

        var left = await Fib(n - 1);
        await Checkpoint($"fib({n}):middle={left}");
        var right = await Fib(n - 2);
        var value = left + right;
        await Checkpoint($"fib({n}):end={value}");
        return value;
    }

    private static async PlaybackTask LinearScenario(string name, List<string> events)
    {
        events.Add($"{name}0");
        await Checkpoint($"{name}:0");
        events.Add($"{name}1");
        await Checkpoint($"{name}:1");
    }

    private static string Joined(List<string> events) => string.Join(",", events);

    private static string[] Labels(Playback playback)
    {
        var records = playback.Records;
        var labels = new string[records.Length];
        for (var i = 0; i < records.Length; i++)
            labels[i] = records[i].Label;
        return labels;
    }
}
