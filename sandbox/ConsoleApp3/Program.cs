using AsyncPlayback;

var playback = Playback.Start(CheckoutWorkflow);

await MoveForwardAsync(playback, "first forward");
foreach (var r in playback.Records)
{
    Console.WriteLine(r);
}

var timelinePath = Path.GetFullPath(Path.Combine("sandbox", "ConsoleApp3", "obj", "timeline.json"));
await File.WriteAllTextAsync(
    timelinePath,
    playback.ExportTimelineJson(
        new TimelineExportOptions { SampleInterval = TimeSpan.FromMilliseconds(50) }
    )
);
Console.WriteLine($"timeline json: {timelinePath}");

await MoveBackwardAsync(playback, "undo");

static async Task MoveForwardAsync(Playback playback, string label)
{
    Console.WriteLine($"-- {label} --");

    var lastState = "";
    while (
        (await playback.TryStepForwardAsync(PlaybackStepGranularity.Logical))
            is { Moved: true } result
    )
    {
        DumpStep(result);
        DumpStoredState(playback, ref lastState);
    }
}

static async Task MoveBackwardAsync(Playback playback, string label)
{
    Console.WriteLine($"-- {label} --");

    var lastState = "";
    while (
        (await playback.TryStepBackAsync(PlaybackStepGranularity.Logical)) is { Moved: true } result
    )
    {
        DumpStep(result);
        DumpStoredState(playback, ref lastState);
    }
}

static async PlaybackTask CheckoutWorkflow(Playback playback)
{
    if (PlaybackTask.IsForward)
        PlaybackTask.Store(await CreateOrderAsync());

    await PlaybackTask.Checkpoint("order created");
    if (TryGetOrderToCancel(out var orderToCancel))
    {
        await CancelOrderAsync(orderToCancel);
        PlaybackTask.ClearStore();
        return;
    }

    if (PlaybackTask.IsForward)
        PlaybackTask.Store(await ReserveInventoryAsync(RequireState()));

    await PlaybackTask.Checkpoint("inventory reserved");
    if (TryGetReservationToRelease(out var reservationToRelease))
    {
        await ReleaseInventoryAsync(reservationToRelease);
        PlaybackTask.Store(reservationToRelease with { ReservationId = null });
        return;
    }

    if (PlaybackTask.IsForward)
        PlaybackTask.Store(await ChargePaymentAsync(RequireState()));

    await PlaybackTask.Checkpoint("payment charged");
    if (TryGetPaymentToRefund(out var paymentToRefund))
    {
        await RefundPaymentAsync(paymentToRefund);
        PlaybackTask.Store(paymentToRefund with { PaymentId = null });
        return;
    }

    if (PlaybackTask.IsForward)
        Console.WriteLine("workflow: checkout is active");

    await PlaybackTask.Checkpoint("checkout active");
}

static async PlaybackTask<CheckoutState> CreateOrderAsync()
{
    var state = await PlaybackTask.Effect(
        async (ct) =>
        {
            await Task.Delay(100);
            Console.WriteLine("effect: create order");
            return new CheckoutState(OrderId: "order-001", Amount: 1200);
        },
        "create order"
    );

    return state;
}

static async PlaybackTask<CheckoutState> ReserveInventoryAsync(CheckoutState state)
{
    var reserved = await PlaybackTask.Effect(
        async (ct) =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: reserve inventory for {state.OrderId}");
            return state with { ReservationId = "reservation-001" };
        },
        "reserve inventory"
    );

    return reserved;
}

static async PlaybackTask<CheckoutState> ChargePaymentAsync(CheckoutState state)
{
    var charged = await PlaybackTask.Effect(
        async (ct) =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: charge {state.Amount} yen");
            return state with { PaymentId = "payment-001" };
        },
        "charge payment"
    );

    return charged;
}

static async PlaybackTask RefundPaymentAsync(CheckoutState state)
{
    await PlaybackTask.Effect(
        async (ct) =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: refund {state.PaymentId}");
        },
        "refund payment"
    );
}

static async PlaybackTask ReleaseInventoryAsync(CheckoutState state)
{
    await PlaybackTask.Effect(
        async (ct) =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: release {state.ReservationId}");
        },
        "release inventory"
    );
}

static async PlaybackTask CancelOrderAsync(CheckoutState state)
{
    await PlaybackTask.Effect(
        async (ct) =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: cancel {state.OrderId}");
        },
        "cancel order"
    );
}

static bool TryGetPaymentToRefund(out CheckoutState state)
{
    if (
        PlaybackTask.IsBackward
        && PlaybackTask.TryGet<CheckoutState>(out var restored)
        && restored is { PaymentId: not null }
    )
    {
        state = restored;
        return true;
    }

    state = default!;
    return false;
}

static bool TryGetReservationToRelease(out CheckoutState state)
{
    var playback = PlaybackTask.GetCurrentPlayback();
    if (
        PlaybackTask.IsBackward
        && playback.TryGet<CheckoutState>(out var restored)
        && restored is { ReservationId: not null }
    )
    {
        state = restored;
        return true;
    }

    state = default!;
    return false;
}

static bool TryGetOrderToCancel(out CheckoutState state)
{
    var playback = PlaybackTask.GetCurrentPlayback();
    if (PlaybackTask.IsBackward && playback.TryGet<CheckoutState>(out var restored))
    {
        state = restored;
        return true;
    }

    state = default!;
    return false;
}

static CheckoutState RequireState()
{
    if (PlaybackTask.TryGet<CheckoutState>(out var state))
        return state;

    throw new InvalidOperationException("Checkout state was not restored.");
}

static void DumpStoredState(Playback playback, ref string lastState)
{
    var currentState = FormatStoredState(playback);
    if (currentState == lastState)
        return;

    Console.WriteLine(currentState);
    lastState = currentState;
}

static void DumpStep(StepResult result)
{
    if (result.DebugLabel == null)
        return;

    Console.WriteLine($"step: {result.DebugLabel} ({result.BoundaryKind})");
}

static string FormatStoredState(Playback playback)
{
    if (!playback.TryGet<CheckoutState>(out var state))
        return "store: <empty>";

    return $"store: order={state.OrderId}, reservation={state.ReservationId ?? "-"}, payment={state.PaymentId ?? "-"}";
}

internal sealed record CheckoutState(
    string OrderId,
    int Amount,
    string? ReservationId = null,
    string? PaymentId = null
);
