using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-26 D-C12: el recorrido de los pedidos que ya estaban importados cuando existió la bandeja de ciudades sin
/// resolver. Con dobles: el espejo, el estado del pedido (Quotations), la geografía y la bandeja son los de siempre en estas
/// pruebas; la integración (<c>ShopifyCityBackfillTests</c>) lo corre contra Postgres por el endpoint del operador.
/// </summary>
public sealed class ShopifyUnresolvedCityBackfillTests
{
    private static readonly Guid ValleId = Guid.Parse("01900000-0000-7000-8000-0000000000d1");
    private static readonly Guid CaliId = Guid.Parse("01900000-0000-7000-8000-0000000000d2");
    private static readonly Guid SynonymId = Guid.Parse("01900000-0000-7000-8000-0000000000d3");

    private static readonly ShopifyAddress Calli = new(
        "Laura Gómez", "Cra 5 # 10-20", null, "CALLI", "Valle del Cauca", "VAC", "CO", "+573001112233");

    private static readonly Dictionary<string, string> Document = new(StringComparer.Ordinal)
    {
        ["Documento"] = "1020304050",
        ["Tipo de documento"] = "CC",
    };

    /// <summary>El estado de un pedido que el recorrido sí reporta: <c>Pending</c>, sin parte de envío, no es de recoger en tienda y
    /// con la línea de la dirección cruda en las notas.</summary>
    private static readonly ShopifyOrderCityState Waiting = new("Pending", false, false, true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APendingOrderWithoutShippingAndTheRawLineIsReported()
    {
        var world = new BackfillWorld();
        var row = world.Imported(ImporterWorld.Order(address: Calli), Waiting);

        var result = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(Scanned: 1, Reported: 1, AlreadyReported: 0, NowMatching: 0, Skipped: 0), result);
        var report = Assert.Single(world.Shop.UnresolvedCities.Reports);
        Assert.Equal(
            (ImporterWorld.TenantId, row.Id, row.QepOrderId!.Value, (Guid?)null, ValleId, "CALLI", "VAC", true, false),
            (report.TenantId, report.MirrorOrderId, report.OrderId, report.CustomerId, report.DepartmentId, report.CityText,
                report.ProvinceCode, report.PartyCityOnly, report.CustomerCityOnly));
        // La misma muestra que el importador (ShopifyUnresolvedCitySample): sólo la dirección, nunca el nombre ni el teléfono.
        Assert.Equal("Cra 5 # 10-20, CALLI, Valle del Cauca, CO", report.SampleAddress);
        Assert.DoesNotContain("Laura", report.SampleAddress, StringComparison.Ordinal);
        Assert.DoesNotContain("+57", report.SampleAddress, StringComparison.Ordinal);
        Assert.Equal(row.ImportedAt, report.SeenAt);
    }

    [Fact]
    public async Task AnApprovedOrderIsSkipped()
    {
        var world = new BackfillWorld();
        world.Imported(ImporterWorld.Order(address: Calli), Waiting with { Status = "Approved" });

        var result = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(1, 0, 0, 0, 1), result);
        Assert.Empty(world.Shop.UnresolvedCities.Reports);
    }

    [Fact]
    public async Task AnOrderWithShippingIsSkipped()
    {
        var world = new BackfillWorld();
        world.Imported(ImporterWorld.Order(address: Calli), Waiting with { HasShippingParty = true });

        var result = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(1, 0, 0, 0, 1), result);
        Assert.Empty(world.Shop.UnresolvedCities.Reports);
    }

    [Fact]
    public async Task NotesWithoutTheRawLineAreSkipped()
    {
        var world = new BackfillWorld();
        world.Imported(ImporterWorld.Order(address: Calli), Waiting with { NotesHaveRawAddress = false });

        var result = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(1, 0, 0, 0, 1), result);
        Assert.Empty(world.Shop.UnresolvedCities.Reports);
    }

    // Recoger en tienda nunca recibe parte de envío (P10); un pedido que ya no existe o una fila sin pedido tampoco cuentan.
    [Fact]
    public async Task AStorePickupOrderAndAMissingOrderAreSkipped()
    {
        var world = new BackfillWorld();
        world.Imported(ImporterWorld.Order(address: Calli, orderId: 5_550_000_000_001L), Waiting with { IsStorePickup = true });
        world.Imported(ImporterWorld.Order(address: Calli, orderId: 5_550_000_000_002L), state: null);

        var result = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(2, 0, 0, 0, 2), result);
        Assert.Empty(world.Shop.UnresolvedCities.Reports);
    }

    // Si ahora casa (por el nombre real o por un sinónimo confirmado después) no se reporta: ya no está sin resolver.
    [Fact]
    public async Task ACityThatNowMatchesIsCountedNotReported()
    {
        var world = new BackfillWorld();
        world.Shop.Geography.Cities[("VALLE DEL CAUCA", "CALLI")] = new ShopifyCityRef(ValleId, CaliId, SynonymId);
        world.Imported(ImporterWorld.Order(address: Calli), Waiting);

        var result = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(1, 0, 0, 1, 0), result);
        Assert.Empty(world.Shop.UnresolvedCities.Reports);
    }

    [Fact]
    public async Task RunningTwiceReportsNothingNew()
    {
        var world = new BackfillWorld();
        world.Imported(ImporterWorld.Order(address: Calli), Waiting);

        var first = await world.Backfill.RunAsync(Ct);
        // Geography es idempotente por la fila del espejo: la segunda vez la ocurrencia ya estaba.
        world.Shop.UnresolvedCities.Outcome = new ShopifyUnresolvedCityResult(ShopifyUnresolvedCityOutcome.AlreadyRecorded, null);
        var second = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(1, 1, 0, 0, 0), first);
        Assert.Equal(new ShopifyCityBackfillResult(1, 0, 1, 0, 0), second);
        // Las dos corridas mandan el mismo reporte: la ocurrencia es única por la fila del espejo.
        Assert.Equal(world.Shop.UnresolvedCities.Reports[0], world.Shop.UnresolvedCities.Reports[1]);
    }

    // Sin departamento (un país que no es CO, una ciudad en blanco) no hay bandeja; un texto que Geography ignora tampoco.
    [Fact]
    public async Task AnOrderWithoutADepartmentOrWithAnIgnoredTextIsSkipped()
    {
        var world = new BackfillWorld();
        world.Imported(
            ImporterWorld.Order(address: Calli with { CountryCode = "US" }, orderId: 5_550_000_000_001L), Waiting);
        world.Imported(
            ImporterWorld.Order(address: Calli with { City = "   " }, orderId: 5_550_000_000_002L), Waiting);
        world.Imported(ImporterWorld.Order(address: null, orderId: 5_550_000_000_003L), Waiting);
        world.Imported(ImporterWorld.Order(address: Calli, orderId: 5_550_000_000_004L), Waiting);
        world.Shop.UnresolvedCities.Outcome = new ShopifyUnresolvedCityResult(ShopifyUnresolvedCityOutcome.Ignored, null);

        var result = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(4, 0, 0, 0, 4), result);
        Assert.Single(world.Shop.UnresolvedCities.Reports);
    }

    // Ruling P15/S4: el recorrido no sabe si el cliente incompleto lo creó esa importación (el importador reutiliza clientes que ya
    // existían, p. ej. uno nacido en WhatsApp en versión 1), así que nunca manda cliente: ni con uno incompleto que la tienda de
    // hoy completaría, ni con uno incompleto cualquiera, ni con uno completo.
    [Fact]
    public async Task HistoricalOccurrencesNeverCarryACustomer()
    {
        var world = new BackfillWorld();
        world.Shop.WithClassification();
        world.Shop.Database.Stores[ImporterWorld.ConnectionId] = world.Shop.Store;
        var incomplete = Guid.CreateVersion7();
        var complete = Guid.CreateVersion7();
        world.Shop.Customers.ById[incomplete] = new ShopifyCustomerRef(incomplete, false, true, Version: 1);
        world.Shop.Customers.ById[complete] = new ShopifyCustomerRef(complete, true, true, Version: 1);
        world.Imported(
            ImporterWorld.Order(address: Calli, attributes: Document, orderId: 5_550_000_000_001L), Waiting, customerId: incomplete);
        world.Imported(
            ImporterWorld.Order(address: Calli, attributes: Document, orderId: 5_550_000_000_002L), Waiting, customerId: complete);
        world.Imported(ImporterWorld.Order(address: Calli, orderId: 5_550_000_000_003L), Waiting);

        var result = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(3, 3, 0, 0, 0), result);
        Assert.All(world.Shop.UnresolvedCities.Reports, report =>
        {
            Assert.Null(report.CustomerId);
            Assert.False(report.CustomerCityOnly);
            Assert.True(report.PartyCityOnly);
        });
    }

    // Más filas que un lote: el recorrido pagina por id y no se salta ni repite ninguna.
    [Fact]
    public async Task EveryRowIsVisitedOnceAcrossBatches()
    {
        var world = new BackfillWorld();
        const int rows = ShopifyUnresolvedCityBackfill.BatchSize + 5;
        for (var index = 0; index < rows; index++)
        {
            world.Imported(ImporterWorld.Order(address: Calli, orderId: 5_550_000_000_000L + index), Waiting);
        }

        var result = await world.Backfill.RunAsync(Ct);

        Assert.Equal(new ShopifyCityBackfillResult(rows, rows, 0, 0, 0), result);
        Assert.Equal(rows, world.Shop.UnresolvedCities.Reports.Select(report => report.MirrorOrderId).Distinct().Count());
    }

    private sealed class BackfillWorld
    {
        public ImporterWorld Shop { get; } = CreateShop();

        public FakeCityCorrectionWriter Writer { get; } = new();

        public ShopifyUnresolvedCityBackfill Backfill =>
            new(
                Shop.Mirrors,
                Shop.Resolver,
                Writer,
                Shop.UnresolvedCities,
                new FakeClock(ImporterWorld.Now));

        /// <summary>Una fila <c>Imported</c> con su pedido; <paramref name="state"/> es lo que Quotations dice de ese pedido
        /// (<c>null</c>: ya no existe).</summary>
        public ShopifyOrderMirror Imported(ShopifyOrderSnapshot snapshot, ShopifyOrderCityState? state, Guid? customerId = null)
        {
            var row = ShopifyOrderMirror.Create(
                ImporterWorld.TenantId,
                ImporterWorld.ConnectionId,
                snapshot.OrderId,
                ShopifyOrderIngestion.WritePayload(snapshot),
                snapshot.UpdatedAt,
                snapshot.ToHeader());
            var orderId = Guid.CreateVersion7();
            row.MarkImported(customerId ?? Guid.CreateVersion7(), orderId, 0m, null, ImporterWorld.Now);
            Shop.Database.Orders[(ImporterWorld.ConnectionId, snapshot.OrderId)] = row;
            if (state is not null)
            {
                Writer.OrderStates[(ImporterWorld.TenantId, orderId)] = state;
            }

            return row;
        }

        private static ImporterWorld CreateShop()
        {
            var shop = new ImporterWorld();
            // El departamento resuelve aunque la ciudad no case (D-C11): sin él no hay bandeja.
            shop.Geography.Departments["VALLE DEL CAUCA"] = ValleId;
            return shop;
        }
    }
}
