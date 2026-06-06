using MinimumPlayback;
using static MinimumPlayback.PlaybackTask;

var playback = Playback.Create(_ => Scenario(6));

while (true)
{
    var keyInfo = Console.ReadKey(true);
    switch (keyInfo.Key)
    {
        case ConsoleKey.RightArrow:
            var movedNext = playback.TryMoveNext();
            break;
        case ConsoleKey.LeftArrow:
            var movedBack = playback.TryMoveBack();
            break;
        case ConsoleKey.D:
            DumpWithCursor(playback);
            break;
        case ConsoleKey.Q:
        case ConsoleKey.Escape:
            Console.WriteLine("Exiting...");
            return;
    }
}
static async PlaybackTask Scenario(int n)
{
    await Checkpoint($"start");
    var value = await Fib(n);
    await Checkpoint($"end={value}");
}

static async PlaybackTask<int> Fib(int n)
{
    if (n <= 1)
    {
        Console.WriteLine($"Computed fib({n}) = {n}" + (IsForward ? " forward" : " backward"));
        return n;
    }

    var value = await Fib(n - 1) + await Fib(n - 2);
    Console.WriteLine($"Computed fib({n}) = {value}" + (IsForward ? " forward" : " backward"));

    return value;
}
static void DumpRecord(PlaybackRecord record)
{
    Console.WriteLine(
        $"{record.Index}:\t{string.Concat(Enumerable.Repeat("| ", record.Depth))}{record.Label} depth={record.Depth} parent={record.ParentIndex}"
    );
}

static void DumpWithCursor(Playback playback)
{
    for (var i = 0; i < playback.Records.Length; i++)
    {
        if (i == playback.Cursor)
        {
            Console.Write("-> ");
        }
        else
        {
            Console.Write("   ");
        }
        DumpRecord(playback.Records[i]);
    }
}
