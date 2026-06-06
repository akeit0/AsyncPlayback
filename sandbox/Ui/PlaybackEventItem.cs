using AsyncPlayback;

namespace Ui;

internal sealed record PlaybackEventItem(
    double TimeSeconds,
    string Message,
    string Tooltip,
    Brush Brush
)
{
    public static PlaybackEventItem FromEvent(PlaybackEvent e)
    {
        var text =
            e.Kind == PlaybackEventKind.BoundaryReached
                ? $"Event : {e.Record.TypeName}:{e.BoundaryKind}:{e.DebugLabel}:{e.Time.TotalSeconds:F1}s"
                : $"Event : {e.Kind}:{e.Record.TypeName}:{e.DebugLabel}:{e.Time.TotalSeconds:F1}s";

        var tooltip =
            $"{e.Kind}\n"
            + $"Record: #{e.Record.Id.Value} {e.Record.TypeName}\n"
            + $"Boundary: {e.BoundaryKind?.ToString() ?? "-"}\n"
            + $"Direction: {e.Direction}\n"
            + $"Time: {e.Time.TotalSeconds:F3}s\n"
            + $"Timestamp: {e.Timestamp}\n"
            + $"Delta: {e.DeltaTime.TotalSeconds:F3}s";

        return new(e.Time.TotalSeconds, text, tooltip, BrushFor(e));
    }

    private static Brush BrushFor(PlaybackEvent e)
    {
        if (e.Kind == PlaybackEventKind.CheckpointAdded)
            return new SolidColorBrush(Color.FromArgb(255, 170, 126, 36));

        return e.BoundaryKind switch
        {
            PlaybackBoundaryKind.Start => new SolidColorBrush(Color.FromArgb(255, 48, 128, 94)),
            PlaybackBoundaryKind.End => new SolidColorBrush(Color.FromArgb(255, 61, 118, 172)),
            PlaybackBoundaryKind.Point => new SolidColorBrush(Color.FromArgb(255, 157, 105, 42)),
            _ => new SolidColorBrush(Color.FromArgb(255, 110, 110, 110)),
        };
    }
}
