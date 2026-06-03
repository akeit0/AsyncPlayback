using AsyncPlayback;

var playback = Playback.Create(Scenario);

Console.WriteLine("-- forward --");
for (var i = 0; i < 8; i++)
    await playback.AdvanceByAsync(TimeSpan.FromSeconds(0.25));

Console.WriteLine("-- back --");
for (var i = 0; i < 5; i++)
{
    await playback.AdvanceByAsync(-TimeSpan.FromSeconds(0.4));
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

static async PlaybackTask Child(Playback playback)
{
    Console.WriteLine("Child started");

    await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(1.0)))
        Console.WriteLine("child loop " + progress.Progress);

    Console.WriteLine("Child ended");
}
