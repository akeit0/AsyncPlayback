using AsyncPlayback;
using static AsyncPlayback.PlaybackTask;

var playback = Playback.Start(Scenario);

Console.WriteLine("-- forward --");
while ((await playback.TryStepForwardAsync()).Moved)
{
    Console.WriteLine("th");
}

Console.WriteLine("-- back --");
while ((await playback.TryStepBackAsync()).Moved)
{
    Console.WriteLine("th");
}

static async PlaybackTask Scenario(Playback playback)
{
    for (var i = 0; i < 5; i++)
    {
        Console.Write(i);
        await Checkpoint();
    }
}
