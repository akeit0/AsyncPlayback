using AsyncPlayback;

namespace Ui;

internal partial record MainModel
{
    private readonly SemaphoreSlim transportGate = new(1, 1);
    private Playback playback;
    private bool running;
    private string labelText = "Hello, world!";
    private double rectWidth;
    private double manualSeekFrontierSeconds;
    private double? activeManualSeekCeilingSeconds;

    public MainModel()
    {
        playback = CreatePlayback();
    }

    public IState<PlaybackUiState> Session => State.Value(this, () => CreateUiState());

    public async ValueTask Run()
    {
        if (running)
            return;

        running = true;
        await PublishAsync();

        playback.ResetTimestamp();
        while (running && !playback.IsCompleted)
        {
            await Task.Delay(16);
            await transportGate.WaitAsync();
            try
            {
                if (!running)
                    break;

                await playback.AdvanceByElapsedTimeAsync();
                await PublishAsync();
            }
            finally
            {
                transportGate.Release();
            }
        }

        running = false;
        await PublishAsync();
    }

    public async ValueTask Stop()
    {
        running = false;
        await PublishAsync();
    }

    public async ValueTask Reset()
    {
        running = false;
        await transportGate.WaitAsync();
        try
        {
            labelText = "Hello, world!";
            rectWidth = 0;
            manualSeekFrontierSeconds = 0;
            activeManualSeekCeilingSeconds = null;
            playback = CreatePlayback();
            await PublishAsync(resetEvents: true);
        }
        finally
        {
            transportGate.Release();
        }
    }

    public async ValueTask MoveToStart()
    {
        await MoveToAsync(TimeSpan.Zero);
    }

    public async ValueTask MoveToSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return;

        var target = Math.Clamp(seconds, 0, PlaybackUiState.GetTimelineExtentSeconds(playback));
        await MoveToAsync(TimeSpan.FromSeconds(target));
    }

    public void BeginManualSeek()
    {
        activeManualSeekCeilingSeconds = playback.Time.TotalSeconds;
        _ = PublishAsync();
    }

    public void EndManualSeek()
    {
        activeManualSeekCeilingSeconds = null;
        _ = PublishAsync();
    }

    public double ClampManualSeekSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return playback.Time.TotalSeconds;

        var ceiling = activeManualSeekCeilingSeconds ?? playback.Time.TotalSeconds;
        var backwardOnlyCeiling = Math.Min(playback.Time.TotalSeconds, ceiling);
        return Math.Clamp(seconds, 0, backwardOnlyCeiling);
    }

    public async ValueTask MoveBackwardToSeconds(double seconds)
    {
        await transportGate.WaitAsync();
        try
        {
            running = false;
            var target = ClampManualSeekSeconds(seconds);

            if (target >= playback.Time.TotalSeconds - 0.001)
            {
                await PublishAsync();
                return;
            }

            await playback.MoveToAsync(TimeSpan.FromSeconds(target));
            await PublishAsync();
        }
        finally
        {
            transportGate.Release();
        }
    }

    public async ValueTask MoveToEnd()
    {
        await transportGate.WaitAsync();
        try
        {
            running = false;
            await playback.RunToEndAsync();
            await PublishAsync();
        }
        finally
        {
            transportGate.Release();
        }
    }

    private async ValueTask MoveToAsync(TimeSpan target)
    {
        await transportGate.WaitAsync();
        try
        {
            running = false;
            await playback.MoveToAsync(target);
            await PublishAsync();
        }
        finally
        {
            transportGate.Release();
        }
    }

    private Playback CreatePlayback()
    {
        var instance = Playback.Start(SimulateWork);
        instance.EventOccurred += OnPlaybackEvent;
        return instance;
    }

    private void OnPlaybackEvent(PlaybackEvent e)
    {
        if (e.Record.Visibility == TimelineRecordVisibility.Infrastructure)
            return;

        _ = Session.UpdateAsync(state =>
        {
            var current = state ?? CreateUiState();
            return current.WithEvent(e, playback, running);
        });
    }

    private void PublishVisual()
    {
        _ = PublishAsync();
    }

    private ValueTask PublishAsync(bool resetEvents = false)
    {
        return Session.UpdateAsync(state =>
        {
            var current = state ?? CreateUiState();
            var events = resetEvents ? [] : current.Events;
            return CreateUiState(events);
        });
    }

    private PlaybackUiState CreateUiState(IReadOnlyList<PlaybackEventItem>? events = null)
    {
        manualSeekFrontierSeconds = Math.Max(manualSeekFrontierSeconds, playback.Time.TotalSeconds);

        return PlaybackUiState.FromPlayback(
            playback,
            running,
            labelText,
            rectWidth,
            ManualSliderMaximumSeconds,
            events
        );
    }

    private double ManualSliderMaximumSeconds =>
        activeManualSeekCeilingSeconds ?? manualSeekFrontierSeconds;
}
