using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ImporterWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-15 §6.2: las precondiciones en su orden. La primera que falla deja la fila <c>Waiting</c> con su motivo y su
/// <c>wait_detail</c> y no se evalúa nada más (cada prueba deja rota también la siguiente, para que se vea quién gana). Los
/// pedidos de prueba y los cancelados antes de importarse se ignoran sin mirar la tienda (§5.4). El resultado del alta (§6.2.4)
/// lo cubre la tarea 14; acá sólo lo que el importador decide antes y alrededor de llamar al escritor.
/// </summary>
public sealed class ShopifyPreconditionsTests
{
    [Fact]
    public async Task NoAdvisorStoreNotConfigured()
    {
        var world = new ImporterWorld { Store = ShopifyStore.Create(ConnectionId, TenantId, Now) };
        world.Members.Active.Clear();
        var snapshot = Order();
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, ShopifyWaitReasons.StoreNotConfigured, null);
        Assert.Equal(0, world.Members.Calls);
        Assert.Empty(world.Writer.Creates);
    }

    [Fact]
    public async Task InactiveAdvisorAdvisorInactive()
    {
        var world = new ImporterWorld();
        world.Members.Active.Clear();
        world.Billing.Account = null;
        var snapshot = Order();
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, ShopifyWaitReasons.AdvisorInactive, null);
        Assert.Equal(0, world.Billing.Calls);
    }

    [Fact]
    public async Task MissingAccountBillingAccountMissing()
    {
        var world = new ImporterWorld();
        world.Billing.Account = null;
        var snapshot = Order(currency: "USD");
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, ShopifyWaitReasons.BillingAccountMissing, null);
        Assert.Empty(world.Customers.Log);
    }

    [Fact]
    public async Task UsdOrderCopAccountCurrencyMismatch()
    {
        var world = new ImporterWorld();
        var inactive = new ShopifyCustomerRef(Guid.CreateVersion7(), true, false);
        world.Customers.ByEmail["laura@verde.co"] = inactive;
        var snapshot = Order(currency: "usd");
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, ShopifyWaitReasons.CurrencyMismatch, """{"orderCurrency":"USD","billingCurrency":"COP"}""");
        Assert.Empty(world.Customers.Log);
    }

    [Fact]
    public async Task FoundInactiveCustomerCustomerInactive()
    {
        var world = new ImporterWorld();
        var inactive = new ShopifyCustomerRef(Guid.Parse("01900000-0000-7000-8000-0000000000c1"), true, false);
        world.Customers.ByEmail["laura@verde.co"] = inactive;
        var snapshot = Order(lines: [new ShopifyLine("NO-EXISTE", 1m, 1_000m, 0m)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, ShopifyWaitReasons.CustomerInactive, """{"customerId":"01900000-0000-7000-8000-0000000000c1"}""");
        Assert.Empty(world.Products.Requests);
        // Un cliente inactivo no se anota: reactivarlo es lo que resuelve la espera, y el reintento lo vuelve a buscar.
        Assert.Null(row.QepCustomerId);
    }

    [Fact]
    public async Task TwoUnknownSkusProductUnmatchedListsBoth()
    {
        var world = new ImporterWorld();
        world.Products.With("C-3", active: false);
        var snapshot = Order(lines:
        [
            new ShopifyLine("A-1", 1m, 1_000m, 0m),
            new ShopifyLine("CAM-001", 2m, 119_000m, 0m),
            new ShopifyLine(" B-2 ", 1m, 1_000m, 0m),
            new ShopifyLine("C-3", 1m, 1_000m, 0m),
        ]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        // C-3 existe pero está inactivo: también es product_unmatched (§6.2.2), y se evaluaron todas las líneas.
        AssertWaiting(row, outcome, ShopifyWaitReasons.ProductUnmatched, """{"skus":["A-1","B-2","C-3"]}""");
        Assert.Equal(["A-1", "CAM-001", "B-2", "C-3"], Assert.Single(world.Products.Requests));
        Assert.Empty(world.Writer.Creates);
        // El cliente se crea sólo cuando ninguna precondición frena: un pedido que espera un producto no deja una ficha suelta.
        Assert.Empty(world.Customers.Incompletes);
        Assert.Empty(world.Customers.Completes);
    }

    [Fact]
    public async Task LineWithoutSkuProductUnmatchedListsNull()
    {
        var world = new ImporterWorld();
        var snapshot = Order(lines: [new ShopifyLine(null, 1m, 1_000m, 0m), new ShopifyLine("CAM-001", 1m, 1_000m, 0m)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, ShopifyWaitReasons.ProductUnmatched, """{"skus":[null]}""");
    }

    [Fact]
    public async Task TestOrderIgnored()
    {
        var world = new ImporterWorld { Store = ShopifyStore.Create(ConnectionId, TenantId, Now) };
        var snapshot = Order(test: true);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Ignored, outcome.State);
        Assert.Equal((ShopifyImportState.Ignored, (string?)null), (row.ImportState, row.WaitReason));
        Assert.Equal(0, world.Members.Calls);
        Assert.Empty(world.Writer.Creates);
    }

    [Fact]
    public async Task CancelledBeforeImportIgnored()
    {
        var world = new ImporterWorld();
        var snapshot = Order(cancelledAt: Now.AddMinutes(-5));
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Ignored, outcome.State);
        Assert.Equal(ShopifyImportState.Ignored, row.ImportState);
        Assert.Empty(world.Customers.Log);
        Assert.Empty(world.Writer.Creates);
    }

    [Fact]
    public async Task AllPreconditionsPassWritesTheOrderAndMarksImported()
    {
        var world = new ImporterWorld();
        var orderId = Guid.CreateVersion7();
        world.Writer.Result = _ => ShopifyOrderWriteResult.Written(orderId, "PED-0007", 118_500m, alreadyExisted: false);
        var snapshot = Order(lines: [new ShopifyLine("CAM-001", 2m, 59_500m, 11_900m)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Imported, outcome.State);
        var (draft, _) = Assert.Single(world.Writer.Creates);
        Assert.Equal(
            (TenantId, "#1001", $"{ShopId}:{snapshot.OrderId}", AdvisorId, "pending", snapshot.CreatedAt),
            (draft.TenantId, draft.ExternalReference, draft.ExternalKey, draft.AdvisorMembershipId, draft.FinancialStatus, draft.OccurredAt));
        Assert.Equal(world.Billing.Account, draft.BillingAccount);
        var line = Assert.Single(draft.Lines);
        Assert.Equal(
            (world.Products.Products["CAM-001"].Id, 2m, 59_500m, 10m, 19),
            (line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage, line.TaxPercentage));
        Assert.Equal((row.QepCustomerId, (Guid?)orderId, (decimal?)500m), (draft.ClientId, row.QepOrderId, row.TotalDifference));
        Assert.Equal((ShopifyImportState.Imported, (string?)null), (row.ImportState, row.WaitReason));
    }

    [Fact]
    public async Task RejectedByTheOrderWaitsOrderRejectedWithItsCode()
    {
        var world = new ImporterWorld();
        world.Writer.Result = _ => ShopifyOrderWriteResult.Rejected("order.item.quantity_invalid");
        // Línea con cantidad 0 (un payload sin quantity, tarea 10): no se importa en silencio; la regla del pedido la rechaza y
        // la fila queda en espera con el código.
        var snapshot = Order(lines: [new ShopifyLine("CAM-001", 0m, 119_000m, 0m)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, ShopifyWaitReasons.OrderRejected, """{"code":"order.item.quantity_invalid"}""");
        Assert.NotNull(row.QepCustomerId);
    }

    // S17: el reintento después de un conflicto en el segundo guardado de la ingesta, o de una caída entre crear el pedido y marcar
    // la fila. Quotations devuelve el pedido que ya existe con esa clave; es un éxito, y el pago que Shopify trae ahora se
    // sincroniza porque el pedido nació con el estado de antes (D-S26).
    [Fact]
    public async Task AlreadyExistedMarksImportedAndSyncsThePayment()
    {
        var world = new ImporterWorld();
        var existing = Guid.CreateVersion7();
        world.Writer.Result = _ => ShopifyOrderWriteResult.Written(existing, "PED-0003", 119_000m, alreadyExisted: true);
        var snapshot = Order(financialStatus: "paid");
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Imported, outcome.State);
        Assert.Equal((ShopifyImportState.Imported, (Guid?)existing), (row.ImportState, row.QepOrderId));
        Assert.Equal([(TenantId, existing, "paid")], world.Writer.Syncs);
    }

    [Fact]
    public async Task WrittenNewOrderDoesNotSyncThePayment()
    {
        var world = new ImporterWorld();
        var snapshot = Order(financialStatus: "paid");
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Empty(world.Writer.Syncs);
    }

    // Lo que el adaptador propaga (RequestConcurrencyException, order.order.number_taken, base caída) no se vuelve espera ni
    // ignorado: la fila no cambia y la entrega reintenta (ruling S17).
    [Fact]
    public async Task WriterFailureIsPropagatedWithoutMarkingTheRow()
    {
        var world = new ImporterWorld();
        world.Writer.OnCreate = () => new InvalidOperationException("Quotations is down.");
        var snapshot = Order();
        var row = world.RowFor(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.ImportAsync(row, snapshot));

        Assert.Equal((ShopifyImportState.Waiting, (string?)null, 0), (row.ImportState, row.WaitReason, row.Attempts));
    }

    [Fact]
    public async Task RowThatIsNoLongerWaitingIsLeftAlone()
    {
        var world = new ImporterWorld();
        var snapshot = Order();
        var row = world.RowFor(snapshot);
        row.MarkIgnored();

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Ignored, outcome.State);
        Assert.Empty(world.Customers.Log);
        Assert.Empty(world.Writer.Creates);
    }

    private static void AssertWaiting(ShopifyOrderMirror row, ShopifyImportOutcome outcome, string reason, string? detail)
    {
        Assert.Equal(ShopifyImportState.Waiting, outcome.State);
        Assert.Equal((ShopifyImportState.Waiting, reason, detail), (row.ImportState, row.WaitReason, row.WaitDetail));
        Assert.Equal((1, (DateTimeOffset?)Now), (row.Attempts, row.LastAttemptAt));
    }
}
