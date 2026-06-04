using AsyncPlayback;

var playback = Playback.Start(Scenario, TimeProvider.System);

Console.WriteLine("-- forward realtime --");
while (!playback.IsCompleted)
{
    await Task.Delay(TimeSpan.FromSeconds(0.25), playback.TimeProvider);
    await playback.AdvanceByElapsedTimeAsync();
}

Console.WriteLine("-- back commanded --");
for (var i = 0; i < 5; i++)
{
    await playback.MoveToAsync(TimeSpan.FromSeconds((4 - i) * 0.4));
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
