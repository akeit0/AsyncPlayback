using AsyncPlayback;

namespace Ui;

internal sealed record TimelineItem(
    string Header,
    string Label,
    string Tooltip,
    double TrackWidth,
    Thickness Margin,
    double Width,
    Brush Brush
)
{
    private const double TrackInset = 8;
    private const double CurrentMarkerWidth = 4;
    private const double MinimumRecordWidth = 8;

    public static IReadOnlyList<TimelineItem> Build(
        IReadOnlyList<TimelineRecordInfo> records,
        TimeSpan currentTime,
        double trackWidth
    )
    {
        if (records.Count == 0)
            return
            [
                new(
                    "Now",
                    "current time",
                    $"Current time\n{currentTime.TotalSeconds:F3}s",
                    trackWidth,
                    new Thickness(CurrentLeft(currentTime, currentTime, trackWidth), 3, 0, 3),
                    CurrentMarkerWidth,
                    new SolidColorBrush(Color.FromArgb(255, 20, 20, 20))
                ),
            ];

        var visibleRecords = records
            .Where(r => r.Visibility != TimelineRecordVisibility.Infrastructure)
            .Where(r => r.StartTime <= currentTime)
            .ToArray();
        var duration = Math.Max(
            2.0,
            Math.Max(
                currentTime.TotalSeconds,
                visibleRecords.Select(r => Min(r.EndTime, currentTime).TotalSeconds).DefaultIfEmpty(2).Max()
            )
        );
        var contentWidth = ContentWidth(trackWidth);
        var result = new List<TimelineItem>(records.Count + 1);

        foreach (var record in visibleRecords)
        {
            var left = TrackInset + record.StartTime.TotalSeconds / duration * contentWidth;
            var effectiveEnd =
                record.Kind == TimelineRecordKind.Checkpoint ? record.EndTime
                : Min(record.EndTime, currentTime);
            var width =
                record.Kind == TimelineRecordKind.Checkpoint
                    ? MinimumRecordWidth
                    : Math.Max(
                        MinimumRecordWidth,
                        Math.Max(0, (effectiveEnd - record.StartTime).TotalSeconds)
                            / duration
                            * contentWidth
                    );
            width = Math.Min(width, Math.Max(MinimumRecordWidth, trackWidth - TrackInset - left));
            left = Math.Min(left, Math.Max(TrackInset, trackWidth - TrackInset - width));

            result.Add(
                new(
                    $"#{record.Id.Value} {record.Kind}",
                    record.DebugLabel,
                    FormatTooltip(record),
                    trackWidth,
                    new Thickness(left, 3, 0, 3),
                    width,
                    BrushFor(record.Kind)
                )
            );
        }

        result.Add(
            new(
                "Now",
                "current time",
                $"Current time\n{currentTime.TotalSeconds:F3}s",
                trackWidth,
                new Thickness(
                    CurrentLeft(currentTime, TimeSpan.FromSeconds(duration), trackWidth),
                    3,
                    0,
                    3
                ),
                4,
                new SolidColorBrush(Color.FromArgb(255, 20, 20, 20))
            )
        );

        return result;
    }

    private static double CurrentLeft(TimeSpan currentTime, TimeSpan duration, double trackWidth)
    {
        var totalSeconds = Math.Max(2.0, duration.TotalSeconds);
        return Math.Clamp(
            TrackInset + currentTime.TotalSeconds / totalSeconds * ContentWidth(trackWidth),
            TrackInset,
            Math.Max(TrackInset, trackWidth - TrackInset - CurrentMarkerWidth)
        );
    }

    private static double ContentWidth(double trackWidth)
    {
        return Math.Max(0, trackWidth - TrackInset * 2);
    }

    private static TimeSpan Min(TimeSpan x, TimeSpan y)
    {
        return x <= y ? x : y;
    }

    private static string FormatTooltip(TimelineRecordInfo record)
    {
        return $"#{record.Id.Value} {record.Kind}\n"
            + $"{record.DebugLabel}\n"
            + $"Start: {record.StartTime.TotalSeconds:F3}s\n"
            + $"End: {record.EndTime.TotalSeconds:F3}s\n"
            + $"Duration: {record.Duration.TotalSeconds:F3}s\n"
            + $"Checkpoint: {record.CheckpointKind?.ToString() ?? "-"}";
    }

    private static Brush BrushFor(TimelineRecordKind kind)
    {
        return kind switch
        {
            TimelineRecordKind.Checkpoint => new SolidColorBrush(Color.FromArgb(255, 214, 158, 46)),
            TimelineRecordKind.Delay => new SolidColorBrush(Color.FromArgb(255, 61, 132, 184)),
            TimelineRecordKind.SeekLoop => new SolidColorBrush(Color.FromArgb(255, 58, 155, 111)),
            TimelineRecordKind.Effect => new SolidColorBrush(Color.FromArgb(255, 185, 83, 87)),
            TimelineRecordKind.Call => new SolidColorBrush(Color.FromArgb(255, 114, 105, 168)),
            _ => new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)),
        };
    }
}
