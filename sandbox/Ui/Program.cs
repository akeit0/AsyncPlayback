using System.Diagnostics;
using System.Threading.Channels;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using AsyncPlayback;

Win32Platform.Register();
GdiBackend.Register();
bool syncingSliderFromPlayback = false;
Playback playback = null!;
bool heldslider = false;
var transportGate = new SemaphoreSlim(1, 1);
var window = new Window()
    .Title("Hello MewUI")
    .Resizable(520, 360)
    .Padding(12)
    .Content(
        new StackPanel()
            .Spacing(8)
            .Ref(out var sp)
            .Children(
                new Label().Text("Hello, world!").FontSize(54).Bold().Ref(out var label),
                new Label().Text("0.0s").FontSize(25).Bold().Ref(out var timeLabel),
                new Slider()
                    .Minimum(0)
                    .Maximum(3.0)
                    .Ref(out var slider)
                    .OnMouseDown(_ => heldslider = true)
                    .OnMouseUp(_ => heldslider = false)
                    .OnValueChanged((value) => SyncSlider(playback, value)),
                new Button()
                    .Content("Run")
                    .OnClick(() =>
                    {
                        Run(playback);
                    })
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
        if (playback.Time.TotalSeconds < value)
        {
            slider.Value = playback.Time.TotalSeconds;
            return;
        }
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
    label.Text = "Work started";
    await playback.Delay(TimeSpan.FromSeconds(0.3));
    var rect = AddRect(playback);
    await playback.Checkpoint("rectangle added");
    await foreach (var progress in playback.ForEachOnSeek(TimeSpan.FromSeconds(2.7)))
    {
        rect.Width = 510 * (1 - progress.Progress);
    }

    label.Text = "Work completed";
}

Rectangle AddRect(Playback playback)
{
    if (playback.CurrentDirection == PlaybackDirection.Forward)
    {
        var rect = new Rectangle().Width(510).Height(100).Fill(Color.Green);
        sp.Add(rect);

        return rect;
    }
    else
    {
        sp.RemoveAt(sp.Children.Count - 1);
        return null!;
    }
}
