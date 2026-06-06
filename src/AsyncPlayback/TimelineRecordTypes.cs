namespace AsyncPlayback;

public static class TimelineRecordTypes
{
    public static class Checkpoint
    {
        public const string Id = "checkpoint";
        public const string Name = "Checkpoint";
    }

    public static class Delay
    {
        public const string Id = "delay";
        public const string Name = "Delay";
    }

    public static class Effect
    {
        public const string Id = "effect";
        public const string Name = "Effect";
    }

    public static class SeekLoop
    {
        public const string Id = "seek-loop";
        public const string Name = "Seek Loop";
    }

    public static class Call
    {
        public const string Id = "call";
        public const string Name = "Call";
    }
}
