using AsyncPlayback;
using static AsyncPlayback.PlaybackTask;

namespace Ui;

internal partial record MainModel
{
    private const double DemoRectMaxWidth = 450;

    private async PlaybackTask SimulateWork(Playback playback)
    {
        var initialText = labelText;

        await Checkpoint("start work");
        SetLabelText(IsForward ? "Running" : initialText);

        await Delay(TimeSpan.FromSeconds(0.7));

        await foreach (var progress in ForEachOnSeek(TimeSpan.FromSeconds(1)))
            SetRectWidth(DemoRectMaxWidth * progress.Progress);

        await Delay(TimeSpan.FromSeconds(0.3));
        SetLabelText(SelectByDirection(backwardStore: labelText, forward: "Done!"));
    }

    private void SetLabelText(string text)
    {
        labelText = text;
        PublishVisual();
    }

    private void SetRectWidth(double width)
    {
        rectWidth = Math.Clamp(width, 0, DemoRectMaxWidth);
        PublishVisual();
    }

    private void PublishVisual()
    {
        _ = PublishAsync();
    }
}
