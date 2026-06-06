namespace AsyncPlayback;

public readonly struct TimelineEvaluationBuilder
{
    private readonly Timeline timeline;
    private readonly int recordIndex;

    internal TimelineEvaluationBuilder(Timeline timeline, int recordIndex)
    {
        this.timeline = timeline;
        this.recordIndex = recordIndex;
    }

    public void AddPoint(TimeSpan time, PlaybackDirection? direction = null) =>
        timeline.AddPointEvaluation(time, recordIndex, direction);

    public void AddRange(
        TimeSpan start,
        TimeSpan end,
        PlaybackDirection? direction = null,
        bool includeEnd = true
    )
    {
        if (end < start)
            throw new ArgumentException("Range end must be greater than or equal to start.");
        timeline.AddRangeEvaluation(start, end, recordIndex, direction, includeEnd);
    }
}
