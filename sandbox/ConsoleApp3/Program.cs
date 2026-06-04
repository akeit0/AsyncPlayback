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
    if (playback.CurrentDirection == PlaybackDirection.Forward)
        playback.Store(await CreateOrderAsync(playback));

    await playback.Checkpoint("order created");
    if (TryGetOrderToCancel(playback, out var orderToCancel))
    {
        await CancelOrderAsync(playback, orderToCancel);
        playback.ClearStore();
        return;
    }

    if (playback.CurrentDirection == PlaybackDirection.Forward)
        playback.Store(await ReserveInventoryAsync(playback, RequireState(playback)));

    await playback.Checkpoint("inventory reserved");
    if (TryGetReservationToRelease(playback, out var reservationToRelease))
    {
        await ReleaseInventoryAsync(playback, reservationToRelease);
        playback.Store(reservationToRelease with { ReservationId = null });
        return;
    }

    if (playback.CurrentDirection == PlaybackDirection.Forward)
        playback.Store(await ChargePaymentAsync(playback, RequireState(playback)));

    await playback.Checkpoint("payment charged");
    if (TryGetPaymentToRefund(playback, out var paymentToRefund))
    {
        await RefundPaymentAsync(playback, paymentToRefund);
        playback.Store(paymentToRefund with { PaymentId = null });
        return;
    }

    if (playback.CurrentDirection == PlaybackDirection.Forward)
        Console.WriteLine("workflow: checkout is active");

    await playback.Checkpoint("checkout active");
}

static async PlaybackTask<CheckoutState> CreateOrderAsync(Playback playback)
{
    var state = await playback.Effect(
        async () =>
        {
            await Task.Delay(100);
            Console.WriteLine("effect: create order");
            return new CheckoutState(OrderId: "order-001", Amount: 1200);
        },
        "create order"
    );

    return state;
}

static async PlaybackTask<CheckoutState> ReserveInventoryAsync(
    Playback playback,
    CheckoutState state
)
{
    var reserved = await playback.Effect(
        async () =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: reserve inventory for {state.OrderId}");
            return state with { ReservationId = "reservation-001" };
        },
        "reserve inventory"
    );

    return reserved;
}

static async PlaybackTask<CheckoutState> ChargePaymentAsync(Playback playback, CheckoutState state)
{
    var charged = await playback.Effect(
        async () =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: charge {state.Amount} yen");
            return state with { PaymentId = "payment-001" };
        },
        "charge payment"
    );

    return charged;
}

static async PlaybackTask RefundPaymentAsync(Playback playback, CheckoutState state)
{
    await playback.Effect(
        async () =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: refund {state.PaymentId}");
        },
        "refund payment"
    );
}

static async PlaybackTask ReleaseInventoryAsync(Playback playback, CheckoutState state)
{
    await playback.Effect(
        async () =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: release {state.ReservationId}");
        },
        "release inventory"
    );
}

static async PlaybackTask CancelOrderAsync(Playback playback, CheckoutState state)
{
    await playback.Effect(
        async () =>
        {
            await Task.Delay(100);
            Console.WriteLine($"effect: cancel {state.OrderId}");
        },
        "cancel order"
    );
}

static bool TryGetPaymentToRefund(Playback playback, out CheckoutState state)
{
    if (
        playback.CurrentDirection == PlaybackDirection.Backward
        && playback.TryGet<CheckoutState>(out var restored)
        && restored is { PaymentId: not null }
    )
    {
        state = restored;
        return true;
    }

    state = default!;
    return false;
}

static bool TryGetReservationToRelease(Playback playback, out CheckoutState state)
{
    if (
        playback.CurrentDirection == PlaybackDirection.Backward
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

static bool TryGetOrderToCancel(Playback playback, out CheckoutState state)
{
    if (
        playback.CurrentDirection == PlaybackDirection.Backward
        && playback.TryGet<CheckoutState>(out var restored)
    )
    {
        state = restored;
        return true;
    }

    state = default!;
    return false;
}

static CheckoutState RequireState(Playback playback)
{
    if (playback.TryGet<CheckoutState>(out var state))
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
