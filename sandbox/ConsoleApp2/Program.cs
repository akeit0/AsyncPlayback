using AsyncPlayback;

var playback = Playback.Create(Scenario);

Console.WriteLine("-- forward --");
while ((await playback.TryMoveNextAsync()).Moved)
{
    Console.WriteLine("th");
}

Console.WriteLine("-- back --");
while ((await playback.TryMoveBackAsync()).Moved)
{
    Console.WriteLine("th");
}

static async PlaybackTask Scenario(Playback playback)
{
    for (var i = 0; i < 5; i++)
    {
        Console.Write(i);
        await playback.Checkpoint();
    }
}
