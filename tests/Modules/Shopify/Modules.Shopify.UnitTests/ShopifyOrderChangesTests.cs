using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ImporterWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-15 §6.3 sobre el punto de enganche de la ingesta (ruling S17): <see cref="ShopifyOrderChange"/> trae la fila con
/// el snapshot nuevo ya aplicado y su estado anterior. La reacción tiene que ser idempotente: se repite dentro del bucle de
/// concurrencia de la ingesta (hasta tres veces por entrega) y cuando el guardado del espejo falla después de que ya actuó en
/// Quotations. Cada repetición se simula como la vería la ingesta: la fila releída de la base, igual que antes del intento.
/// </summary>
public sealed class ShopifyOrderChangesTests
{
    private static readonly Guid OrderId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid ClientId = Guid.Parse("01900000-0000-7000-8000-0000000000a2");
    private static readonly DateTimeOffset CancelledAt = Now.AddMinutes(-1);

    private static ShopifyOrderChanges Changes(ImporterWorld world) => new(world.Writer, new FakeClock(Now));

    /// <summary>La fila como está guardada antes del cambio: importada (o en el estado pedido) con el pago de antes.</summary>
    private static ShopifyOrderMirror StoredRow(ShopifyImportState state, string financialStatus = "pending")
    {
        var before = Order(financialStatus: financialStatus);
        var row = ShopifyOrderMirror.Create(TenantId, ConnectionId, before.OrderId, "{}", before.UpdatedAt, before.ToHeader());
        switch (state)
        {
            case ShopifyImportState.Imported:
                row.MarkImported(ClientId, OrderId, 0m, null, Now.AddHours(-1));
                break;
            case ShopifyImportState.Cancelled:
                row.MarkImported(ClientId, OrderId, 0m, null, Now.AddHours(-1));
                row.MarkCancelled(null);
                break;
            case ShopifyImportState.Ignored:
                row.MarkIgnored();
                break;
        }

        return row;
    }

    /// <summary>Lo que hace la ingesta: lee el estado anterior, aplica el snapshot y llama a la reacción.</summary>
    private static async Task<ShopifyOrderMirror> ReactAsync(ImporterWorld world, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot)
    {
        var previous = ShopifyMirrorState.Of(row);
        var later = snapshot with { UpdatedAt = Now };
        row.ApplySnapshot("{}", later.UpdatedAt, later.ToHeader());
        await Changes(world).ReactAsync(
            new ShopifyOrderChange(world.Connection, world.Store, row, previous, later), TestContext.Current.CancellationToken);
        return row;
    }

    // ---- Pago ----

    [Fact]
    public async Task ImportedNewFinancialStatusSyncs()
    {
        var world = new ImporterWorld();

        var row = await ReactAsync(world, StoredRow(ShopifyImportState.Imported), Order(financialStatus: "paid"));

        Assert.Equal([(TenantId, OrderId, "paid")], world.Writer.Syncs);
        Assert.Equal((ShopifyImportState.Imported, (string?)null), (row.ImportState, row.LastError));
    }

    // Un aviso con el mismo estado (cambió otra cosa en Shopify) no llama a Quotations.
    [Fact]
    public async Task ImportedSameFinancialStatusDoesNotSync()
    {
        var world = new ImporterWorld();

        await ReactAsync(world, StoredRow(ShopifyImportState.Imported, "paid"), Order(financialStatus: "PAID"));

        Assert.Empty(world.Writer.Syncs);
    }

    // §6.3: order.order.payment_has_proofs y order.order.not_pending se auditan en last_error y se ignoran.
    [Theory]
    [InlineData("order.order.not_pending")]
    [InlineData("order.order.payment_has_proofs")]
    public async Task SyncIgnoredRecordsLastError(string code)
    {
        var world = new ImporterWorld();
        world.Writer.SyncOutcome = ShopifyOrderOutcome.Ignored(code);

        var row = await ReactAsync(world, StoredRow(ShopifyImportState.Imported), Order(financialStatus: "paid"));

        Assert.Equal((ShopifyImportState.Imported, code), (row.ImportState, row.LastError));
    }

    // Ruling S19: refunded, partially_refunded y voided no cambian nada, ni siquiera last_error.
    [Fact]
    public async Task SyncUnchangedLeavesNoTrace()
    {
        var world = new ImporterWorld();
        world.Writer.SyncOutcome = ShopifyOrderOutcome.Unchanged;
        var row = StoredRow(ShopifyImportState.Imported, "paid");
        var versionAfterSnapshot = row.Version + 1;

        await ReactAsync(world, row, Order(financialStatus: "refunded"));

        Assert.Equal(((string?)null, versionAfterSnapshot), (row.LastError, row.Version));
    }

    // Idempotencia: la misma entrega reintentada (bucle de concurrencia, guardado fallido) vuelve a ver pending → paid. Quotations
    // ya tiene paid: no cambia nada y la fila no gana nada que la reacción no haya puesto la primera vez.
    [Fact]
    public async Task RepeatedPaymentSyncDoesNotDuplicateAnything()
    {
        var world = new ImporterWorld();
        var snapshot = Order(financialStatus: "paid");

        var attempts = new List<ShopifyOrderMirror>();
        for (var attempt = 0; attempt < ShopifyOrderIngestion.MaxConcurrencyAttempts; attempt++)
        {
            attempts.Add(await ReactAsync(world, StoredRow(ShopifyImportState.Imported), snapshot));
        }

        Assert.Equal(1, world.Writer.EffectiveSyncs);
        Assert.All(attempts, row => Assert.Equal((ShopifyImportState.Imported, (string?)null), (row.ImportState, row.LastError)));
        Assert.Single(attempts.Select(row => row.Version).Distinct());
    }

    // ---- Cancelación ----

    [Fact]
    public async Task ImportedCancelledCancelsWithReason()
    {
        var world = new ImporterWorld();

        var row = await ReactAsync(world, StoredRow(ShopifyImportState.Imported), Order(cancelledAt: CancelledAt, cancelReason: "customer"));

        Assert.Equal([(TenantId, OrderId, AdvisorId, "Cancelado en Shopify: customer")], world.Writer.Cancels);
        Assert.Equal((ShopifyImportState.Cancelled, (string?)null, (string?)null), (row.ImportState, row.WaitDetail, row.LastError));
    }

    [Fact]
    public async Task ImportedCancelledWithoutReasonSaysOnlyShopify()
    {
        var world = new ImporterWorld();

        await ReactAsync(world, StoredRow(ShopifyImportState.Imported), Order(cancelledAt: CancelledAt, cancelReason: "  "));

        Assert.Equal("Cancelado en Shopify", Assert.Single(world.Writer.Cancels).Reason);
    }

    // §6.3: ya facturado, QEP no lo anula. La fila pasa a Cancelled con invoiced_before_cancel para que la bandeja la muestre y
    // alguien revierta la factura y anule a mano.
    [Fact]
    public async Task ImportedCancelledButInvoicedMarksCancelledInvoicedBeforeCancel()
    {
        var world = new ImporterWorld();
        world.Writer.CancelOutcome = ShopifyOrderOutcome.AlreadyInvoiced;

        var row = await ReactAsync(world, StoredRow(ShopifyImportState.Imported), Order(cancelledAt: CancelledAt));

        Assert.Equal((ShopifyImportState.Cancelled, """{"reason":"invoiced_before_cancel"}"""), (row.ImportState, row.WaitDetail));
    }

    // Tarea 12: cualquier otro rechazo de la anulación queda Ignored (decisión pendiente del owner) y se audita en last_error;
    // la fila sigue importada.
    [Fact]
    public async Task CancelIgnoredRecordsLastErrorAndStaysImported()
    {
        var world = new ImporterWorld();
        world.Writer.CancelOutcome = ShopifyOrderOutcome.Ignored("order.order.not_found");

        var row = await ReactAsync(world, StoredRow(ShopifyImportState.Imported), Order(cancelledAt: CancelledAt));

        Assert.Equal((ShopifyImportState.Imported, "order.order.not_found"), (row.ImportState, row.LastError));
    }

    // Cancelado y pagado en el mismo aviso: sólo se anula. Sincronizar el pago de un pedido anulado daría not_pending.
    [Fact]
    public async Task CancelledAndPaidAtOnceOnlyCancels()
    {
        var world = new ImporterWorld();

        await ReactAsync(world, StoredRow(ShopifyImportState.Imported), Order(financialStatus: "paid", cancelledAt: CancelledAt));

        Assert.Equal(["cancel"], world.Writer.Log);
    }

    // Idempotencia: la anulación ya hecha en Quotations, con el guardado del espejo perdido. El reintento recibe
    // order.order.already_cancelled y no hace nada en Quotations; la fila sí termina Cancelled, porque si no quedaría
    // importada con el pedido de QEP anulado y ningún aviso posterior la corregiría.
    [Fact]
    public async Task RepeatedCancellationDoesNotDuplicateAnything()
    {
        var world = new ImporterWorld();
        var snapshot = Order(cancelledAt: CancelledAt);

        var attempts = new List<ShopifyOrderMirror>();
        for (var attempt = 0; attempt < ShopifyOrderIngestion.MaxConcurrencyAttempts; attempt++)
        {
            attempts.Add(await ReactAsync(world, StoredRow(ShopifyImportState.Imported), snapshot));
        }

        Assert.Equal([OrderId], world.Writer.CancelledOrders);
        Assert.All(attempts, row => Assert.Equal(
            (ShopifyImportState.Cancelled, (string?)null, (string?)null), (row.ImportState, row.WaitDetail, row.LastError)));
    }

    // ---- Filas en espera y terminales ----

    // §6.3: cancelado sobre una fila en espera → Ignored, si de verdad no llegó a QEP.
    [Fact]
    public async Task WaitingCancelledIgnored()
    {
        var world = new ImporterWorld();

        var row = await ReactAsync(world, StoredRow(ShopifyImportState.Waiting), Order(cancelledAt: CancelledAt));

        Assert.Equal(ShopifyImportState.Ignored, row.ImportState);
        Assert.Equal([(TenantId, $"{ShopId}:{row.ShopifyOrderId}")], world.Writer.KeyLookups);
        Assert.Empty(world.Writer.Cancels);
    }

    // Hueco de la tarea 13: el worker cayó entre crear el pedido en QEP y marcar la fila, que siguió Waiting. Ignorarla dejaría
    // vivo el pedido de QEP de un pedido que Shopify canceló: se trata como importada y se anula.
    [Fact]
    public async Task WaitingCancelledWithTheOrderAlreadyInQepCancelsIt()
    {
        var world = new ImporterWorld();
        var existing = ShopifyOrderWriteResult.Written(OrderId, "PED-0042", 100_000m, alreadyExisted: true, ClientId);
        world.Writer.ByExternalKey[$"{ShopId}:{Order().OrderId}"] = existing;

        var row = await ReactAsync(world, StoredRow(ShopifyImportState.Waiting), Order(cancelledAt: CancelledAt, cancelReason: "fraud"));

        Assert.Equal([(TenantId, OrderId, AdvisorId, "Cancelado en Shopify: fraud")], world.Writer.Cancels);
        Assert.Equal(
            (ShopifyImportState.Cancelled, (Guid?)OrderId, (Guid?)ClientId, (decimal?)19_000m),
            (row.ImportState, row.QepOrderId, row.QepCustomerId, row.TotalDifference));
    }

    // Un cambio de pago sobre una fila en espera no toca Quotations: el pedido todavía no existe y nacerá con el estado nuevo.
    [Fact]
    public async Task WaitingNewFinancialStatusDoesNothing()
    {
        var world = new ImporterWorld();

        var row = await ReactAsync(world, StoredRow(ShopifyImportState.Waiting), Order(financialStatus: "paid"));

        Assert.Empty(world.Writer.Log);
        Assert.Empty(world.Writer.KeyLookups);
        Assert.Equal(ShopifyImportState.Waiting, row.ImportState);
    }

    // Ruling S13: Cancelled e Ignored son terminales; ningún aviso posterior los mueve ni llama a Quotations.
    [Theory]
    [InlineData(ShopifyImportState.Cancelled)]
    [InlineData(ShopifyImportState.Ignored)]
    public async Task TerminalRowsDoNothing(ShopifyImportState state)
    {
        var world = new ImporterWorld();

        var row = await ReactAsync(world, StoredRow(state), Order(financialStatus: "paid", cancelledAt: CancelledAt));

        Assert.Equal(state, row.ImportState);
        Assert.Empty(world.Writer.Log);
        Assert.Empty(world.Writer.KeyLookups);
    }

    // Mismo guardia que el importador: mezclar tienda, fila y conexión de tenants distintos es un error de programación.
    [Fact]
    public async Task ChangeOfAnotherTenantIsAProgrammingError()
    {
        var world = new ImporterWorld { Store = ShopifyStore.Create(ConnectionId, Guid.CreateVersion7(), Now) };

        await Assert.ThrowsAsync<ArgumentException>(
            () => ReactAsync(world, StoredRow(ShopifyImportState.Imported), Order(cancelledAt: CancelledAt)));

        Assert.Empty(world.Writer.Log);
    }
}
