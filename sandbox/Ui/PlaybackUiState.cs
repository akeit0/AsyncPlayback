using AsyncPlayback;

namespace Ui;

internal partial record PlaybackUiState(
    string LabelText,
    bool IsRunning,
    bool IsCompleted,
    double TimeSeconds,
    double TargetSeconds,
    double DurationSeconds,
    double RectWidth,
    double TimelineWidth,
    IReadOnlyList<TimelineItem> Records,
    IReadOnlyList<PlaybackEventItem> Events
)
{
    public string TimeText => $"{TimeSeconds:F1}s";
    public string TargetText => $"{TargetSeconds:F1}s";
    public string CompletionText =>
        IsCompleted ? "Completed"
        : IsRunning ? "Running"
        : "Idle";

    public static PlaybackUiState FromPlayback(
        Playback playback,
        bool isRunning,
        string labelText,
        double rectWidth,
        IReadOnlyList<PlaybackEventItem>? events = null
    )
    {
        const double timelineWidth = 450.0;
        var duration = Math.Max(2.0, GetTimelineExtentSeconds(playback));
        var visibleEvents = CutEventsAt(events, playback.Time);
        return new(
            labelText,
            isRunning,
            playback.IsCompleted,
            playback.Time.TotalSeconds,
            playback.TargetTime.TotalSeconds,
            duration,
            Math.Clamp(rectWidth, 0, 450),
            timelineWidth,
            TimelineItem.Build(playback.Records, playback.Time, timelineWidth),
            visibleEvents
        );
    }

    public static double GetTimelineExtentSeconds(Playback playback)
    {
        var recordEnd = playback
            .Records.Where(r => r.Visibility != TimelineRecordVisibility.Infrastructure)
            .Select(r => r.EndTime.TotalSeconds)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Max(playback.Time.TotalSeconds, recordEnd);
    }

    public PlaybackUiState WithEvent(PlaybackEvent e, Playback playback, bool isRunning)
    {
        var item = PlaybackEventItem.FromEvent(e);
        var events = Events.Concat([item]).TakeLast(160).ToArray();

        return FromPlayback(playback, isRunning, LabelText, RectWidth, events);
    }

    private static IReadOnlyList<PlaybackEventItem> CutEventsAt(
        IReadOnlyList<PlaybackEventItem>? events,
        TimeSpan currentTime
    )
    {
        if (events is not { Count: > 0 })
            return [];

        var currentSeconds = currentTime.TotalSeconds + 0.000_001;
        return events.Where(e => e.TimeSeconds <= currentSeconds).ToArray();
    }
}
