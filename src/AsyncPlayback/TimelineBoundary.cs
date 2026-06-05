namespace AsyncPlayback;

internal enum TimelineBoundaryKind
{
    Point,
    Start,
    End,
}

internal enum TimelineBoundaryScope
{
    Traversal,
    StepForward,
    StepBackward,
}

internal readonly record struct TimelineBoundary(
    TimeSpan Time,
    int Order,
    int RecordIndex,
    TimelineBoundaryKind Kind
) : IComparable<TimelineBoundary>
{
    public static TimelineBoundary Create(TimelineRecord record, TimelineBoundaryKind kind)
    {
        var time = kind == TimelineBoundaryKind.End ? record.EndTime : record.StartTime;
        var order = record.FlatIndex * 2 + (kind == TimelineBoundaryKind.End ? 1 : 0);
        return new(time, order, record.FlatIndex, kind);
    }

    public int CompareTo(TimelineBoundary other)
    {
        var timeComparison = Time.CompareTo(other.Time);
        return timeComparison != 0 ? timeComparison : Order.CompareTo(other.Order);
    }
}
