using AsyncPlayback;
using Microsoft.Extensions.Logging;
using Uno.Extensions;

namespace Ui;

internal partial record MainModel
{
    private readonly SemaphoreSlim transportGate = new(1, 1);
    private Playback playback;
    private bool running;
    private string labelText = "Hello, world!";
    private double rectWidth;
    private double? activeManualSeekCeilingSeconds;
    private readonly ILogger logger = LogExtensionPoint.AmbientLoggerFactory.CreateLogger(
        "MainModel"
    );

    public MainModel()
    {
        playback = CreatePlayback();
    }

    public event Action<PlaybackUiState>? StateChanged;

    public PlaybackUiState Snapshot => CreateUiState();

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
    }

    public void EndManualSeek()
    {
        activeManualSeekCeilingSeconds = null;
    }

    public double ClampManualSeekSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return playback.Time.TotalSeconds;

        var ceiling = activeManualSeekCeilingSeconds ?? playback.Time.TotalSeconds;
        return Math.Clamp(seconds, 0, ceiling);
    }

    public async ValueTask MoveManualToSeconds(double seconds)
    {
        await transportGate.WaitAsync();
        try
        {
            running = false;
            var target = ClampManualSeekSeconds(seconds);

            if (Math.Abs(target - playback.Time.TotalSeconds) < 0.001)
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
            var next = current.WithEvent(e, playback, running);
            StateChanged?.Invoke(next);
            return next;
        });
    }

    private async ValueTask PublishAsync(bool resetEvents = false)
    {
        PlaybackUiState? next = null;
        await Session.UpdateAsync(state =>
        {
            var current = state ?? CreateUiState();
            var events = resetEvents ? [] : current.Events;
            next = CreateUiState(events);
            return next;
        });

        if (next != null)
            StateChanged?.Invoke(next);
    }

    private PlaybackUiState CreateUiState(IReadOnlyList<PlaybackEventItem>? events = null)
    {
        return PlaybackUiState.FromPlayback(playback, running, labelText, rectWidth, events);
    }
}
