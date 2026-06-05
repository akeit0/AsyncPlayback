namespace Ui;

public sealed partial class MainPage : Page
{
    private readonly MainModel model = new();
    private bool sliderInputActive;
    private bool sliderKeyboardActive;
    private bool sliderSeekRunning;
    private double pendingSliderSeconds;

    public MainPage()
    {
        InitializeComponent();
        DataContext = CreateViewModel(model);

        TransportSlider.AddHandler(
            PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(TransportSlider_OnPointerPressed),
            handledEventsToo: true
        );
        TransportSlider.AddHandler(
            PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(TransportSlider_OnPointerReleased),
            handledEventsToo: true
        );
        TransportSlider.AddHandler(
            PointerMovedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(TransportSlider_OnPointerMoved),
            handledEventsToo: true
        );
        TransportSlider.AddHandler(
            PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(TransportSlider_OnPointerCaptureLost),
            handledEventsToo: true
        );
        TransportSlider.Tapped += TransportSlider_OnTapped;
        TransportSlider.GotFocus += TransportSlider_OnGotFocus;
        TransportSlider.LostFocus += TransportSlider_OnLostFocus;
    }

    private static MainViewModel CreateViewModel(MainModel model)
    {
        return (MainViewModel)
            Activator.CreateInstance(
                typeof(MainViewModel),
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                args: [model],
                culture: null
            )!;
    }

    private void TransportSlider_OnPointerPressed(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e
    )
    {
        sliderInputActive = true;
        QueueSliderSeek(TransportSlider.Value);
    }

    private void TransportSlider_OnPointerReleased(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e
    )
    {
        QueueSliderSeek(TransportSlider.Value);
        sliderInputActive = false;
    }

    private void TransportSlider_OnPointerMoved(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e
    )
    {
        if (!sliderInputActive)
            return;

        QueueSliderSeek(TransportSlider.Value);
    }

    private void TransportSlider_OnPointerCaptureLost(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e
    )
    {
        if (!sliderInputActive)
            return;

        QueueSliderSeek(TransportSlider.Value);
        sliderInputActive = false;
    }

    private void TransportSlider_OnTapped(
        object sender,
        Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e
    )
    {
        QueueSliderSeek(TransportSlider.Value);
    }

    private void TransportSlider_OnGotFocus(object sender, RoutedEventArgs e)
    {
        sliderKeyboardActive = true;
    }

    private void TransportSlider_OnLostFocus(object sender, RoutedEventArgs e)
    {
        sliderKeyboardActive = false;
    }

    private void TransportSlider_OnValueChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e
    )
    {
        if (!sliderInputActive && !sliderKeyboardActive)
            return;

        QueueSliderSeek(e.NewValue);
    }

    private void QueueSliderSeek(double seconds)
    {
        var allowedSeconds = model.ClampManualSeekSeconds(seconds);
        pendingSliderSeconds = allowedSeconds;

        if (Math.Abs(TransportSlider.Value - allowedSeconds) >= 0.001)
            TransportSlider.Value = allowedSeconds;

        if (sliderSeekRunning)
            return;

        sliderSeekRunning = true;
        _ = DrainSliderSeekAsync();
    }

    private async Task DrainSliderSeekAsync()
    {
        var lastAppliedSeconds = pendingSliderSeconds;

        try
        {
            while (true)
            {
                var seconds = pendingSliderSeconds;
                lastAppliedSeconds = seconds;
                await model.MoveBackwardToSeconds(seconds);

                if (Math.Abs(seconds - pendingSliderSeconds) < 0.001)
                    return;
            }
        }
        finally
        {
            sliderSeekRunning = false;

            if (Math.Abs(lastAppliedSeconds - pendingSliderSeconds) >= 0.001)
                QueueSliderSeek(pendingSliderSeconds);
        }
    }
}
