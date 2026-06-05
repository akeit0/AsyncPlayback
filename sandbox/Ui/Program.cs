using System.Diagnostics;
using System.Threading.Channels;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using AsyncPlayback;
using static AsyncPlayback.PlaybackTask;

Win32Platform.Register();
GdiBackend.Register();
bool syncingSliderFromPlayback = false;
Playback playback = null!;
bool heldslider = false;
var transportGate = new SemaphoreSlim(1, 1);
var window = new Window()
    .Resizable(460, 360)
    .Padding(12)
    .Content(
        new StackPanel()
            .Spacing(8)
            .Ref(out var sp)
            .Children(
                new Label().Text("Hello, world!").FontSize(30).Bold().Ref(out var label),
                new Label().Text("0.0s").FontSize(25).Bold().Ref(out var timeLabel),
                new Slider()
                    .Minimum(0)
                    .Maximum(2.0)
                    .Ref(out var slider)
                    .OnMouseDown(_ => heldslider = true)
                    .OnMouseUp(_ => heldslider = false)
                    .OnValueChanged((value) => SyncSlider(playback, value)),
                new Button()
                    .Content("Run")
                    .OnClick(() =>
                    {
                        Run(playback);
                    }),
                new Rectangle().Width(0).Height(30).Fill(Color.LightGray).Ref(out var rect)
            )
    );
playback = Playback.Start(SimulateWork);
Application.Run(window);

async void Run(Playback playback)
{
    playback.ResetTimestamp();
    while (!playback.IsCompleted)
    {
        await Task.Delay(16);
        if (heldslider)
            break;

        await transportGate.WaitAsync();
        try
        {
            if (heldslider)
                break;

            await playback.AdvanceByElapsedTimeAsync();

            syncingSliderFromPlayback = true;
            slider.Value = playback.Time.TotalSeconds;
            timeLabel.Text = $"{playback.Time.TotalSeconds:F1}s";
        }
        finally
        {
            syncingSliderFromPlayback = false;
            transportGate.Release();
        }
    }
}
async void SyncSlider(Playback playback, double value)
{
    if (playback == null || syncingSliderFromPlayback)
        return;

    await transportGate.WaitAsync();
    try
    {
        await playback.MoveToAsync(TimeSpan.FromSeconds(value));
        timeLabel.Text = $"{playback.Time.TotalSeconds:F1}s";
    }
    finally
    {
        transportGate.Release();
    }
}

async PlaybackTask SimulateWork(Playback playback)
{
    var initialText = label.Text;
    await Checkpoint();
    label.Text = CurrentDirection == PlaybackDirection.Forward ? "Running" : initialText;
    await Delay(TimeSpan.FromSeconds(1));

    await foreach (var progress in ForEachOnSeek(TimeSpan.FromSeconds(1)))
    {
        rect.Width = 450 * progress.Progress;
    }

    label.Text = SelectByDirection(backwardStore: label.Text, forward: "Done!");
}
