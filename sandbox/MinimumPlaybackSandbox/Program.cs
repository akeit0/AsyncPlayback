using MinimumPlayback;
using static MinimumPlayback.PlaybackTask;

var playback1 = Playback.Create(playback => Scenario(playback, 6));
var playback2 = Playback.Create(playback => Scenario(playback, 4));

for (var i = 0; i < 128 && (!playback1.IsCompleted || !playback2.IsCompleted); i++)
{
    var moveBackTurn = i % 3 == 2;
    if ((i & 1) == 0)
    {
        if (!playback1.IsCompleted)
        {
            if (moveBackTurn)
                MoveBack(playback1, "p1");
            else
                MoveNext(playback1, "p1");
        }
    }
    else if (!playback2.IsCompleted)
    {
        if (moveBackTurn)
            MoveBack(playback2, "p2");
        else
            MoveNext(playback2, "p2");
    }
}

Console.WriteLine("-- records p1 --");
Dump(playback1);
Console.WriteLine("-- records p2 --");
Dump(playback2);

static async PlaybackTask Scenario(Playback playback, int n)
{
    await Checkpoint($"start");
    var value = await Fib(n);
    await Checkpoint($"end={value}");
}

static async PlaybackTask<int> Fib(int n)
{
    if (n <= 1)
    {
        return n;
    }

    var left = await Fib(n - 1);
    await Checkpoint($"fib({n}):middle={left}");
    var right = await Fib(n - 2);
    var value = left + right;
    await Checkpoint($"fib({n}):end={value}");
    return value;
}

static void MoveNext(Playback playback, string tag)
{
    var moved = playback.TryMoveNext();
    Console.WriteLine($"{tag} next -> {Format(playback, moved)}");
}

static void MoveBack(Playback playback, string tag)
{
    var moved = playback.TryMoveBack();
    Console.WriteLine($"{tag} back -> {(moved ? "<cleared>" : "<stop>")}");
}

static string Format(Playback playback, bool moved)
{
    return moved ? playback.Current?.Label ?? "<null>" : "<stop>";
}

static void Dump(Playback playback)
{
    foreach (var record in playback.Records)
        Console.WriteLine(
            $"{record.Index}:\t{string.Concat(Enumerable.Repeat("| ", record.Depth))}{record.Role} {record.Label} depth={record.Depth} parent={record.ParentIndex}"
        );
}
