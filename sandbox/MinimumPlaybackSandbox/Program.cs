using MinimumPlayback;

var playback1 = Playback.Start(playback => Scenario(playback, "A", 6));
var playback2 = Playback.Start(playback => Scenario(playback, "B", 3));

MoveNext(playback1, "p1");
MoveNext(playback1, "p1");
MoveNext(playback2, "p2");
MoveBack(playback1, "p1");
MoveNext(playback2, "p2");
MoveNext(playback1, "p1");
MoveNext(playback2, "p2");
MoveBack(playback2, "p2");
MoveNext(playback1, "p1");
MoveNext(playback2, "p2");

for (var i = 0; i < 128 && (!playback1.IsCompleted || !playback2.IsCompleted); i++)
{
    if ((i & 1) == 0)
    {
        if (!playback1.IsCompleted)
            MoveNext(playback1, "p1");
    }
    else if (!playback2.IsCompleted)
    {
        MoveNext(playback2, "p2");
    }
}

Console.WriteLine("-- records p1 --");
Dump(playback1);
Console.WriteLine("-- records p2 --");
Dump(playback2);

static async PlaybackTask Scenario(Playback playback, string name, int n)
{
    await playback.Checkpoint($"{name}:start");
    var value = await Fib(playback, name, n);
    await playback.Checkpoint($"{name}:end={value}");
}

static async PlaybackTask<int> Fib(Playback playback, string name, int n)
{
    await playback.Checkpoint($"{name}:fib({n}):enter");

    if (n <= 1)
    {
        return n;
    }

    var left = await Fib(playback, name, n - 1);
    await playback.Checkpoint($"{name}:fib({n}):middle={left}");
    var right = await Fib(playback, name, n - 2);
    var value = left + right;
    await playback.Checkpoint($"{name}:fib({n}):exit={value}");
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
            $"{record.Index}: {record.Role} {record.Label} depth={record.Depth} parent={record.ParentIndex}"
        );
}
