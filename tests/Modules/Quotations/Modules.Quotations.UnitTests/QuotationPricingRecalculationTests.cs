using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La pasada que faltaba: resolver el descuento de todas las líneas juntas contra el catálogo y
/// bajarlo al agregado, con la compuerta de compra mínima encima.
/// </summary>
public sealed class QuotationPricingRecalculationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    // Tres tramos: el primero sin descuento, el segundo agrupable, el tercero de volumen.
    private static QuotationPriceScaleRef[] Tiers() =>
    [
        new(1, 5, 0m, QuotationPriceScaleRestriction.Multiple, 1, null, false),
        new(6, 48, 5m, QuotationPriceScaleRestriction.Multiple, 3, null, true),
        new(49, 200, 10m, QuotationPriceScaleRestriction.Multiple, 1, null, false)
    ];

    // Un solo tramo plano con descuento, para los casos donde lo que se prueba es la compuerta y
    // no la agrupación.
    private static QuotationPriceScaleRef[] FlatTier() =>
        [new(1, 999, 5m, QuotationPriceScaleRestriction.Multiple, 1, null, false)];

    private static QuotationProductPricingRef Product(
        Guid id, decimal? cop, decimal? usd, QuotationPriceScaleRef[] scales) =>
        new(id, TenantId, "Producto", true, cop, usd, scales, 19);

    private static Quotation NewQuotation(string currency = "COP") =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            new DateOnly(2026, 10, 31),
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "12345678",
                Currency = currency
            },
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static QuotationItemId AddItem(
        Quotation quotation, Guid productId, decimal quantity, decimal unitPrice)
    {
        var itemId = QuotationItemId.New();

        // Entra sin descuento a propósito: lo que se prueba es que la pasada lo resuelva, no que
        // el llamador lo haya traído.
        quotation.AddItem(itemId, productId, quantity, unitPrice, 0m, 19, AdvisorId, Now);
        return itemId;
    }

    private static decimal DiscountOf(Quotation quotation, QuotationItemId itemId) =>
        quotation.Items.Single(item => item.Id == itemId).DiscountPercentage;

    // El caso que motivó todo: dos productos con 3 unidades cada uno, ninguno llega al tramo 6-48
    // solo, y juntos sí. Y con 6 unidades la compuerta pasa por la rama de cantidad, así que el
    // descuento sobrevive.
    [Fact]
    public async Task GroupingAcrossProductsReachesTheTierThroughTheFullPass()
    {
        var productA = Guid.CreateVersion7();
        var productB = Guid.CreateVersion7();
        var quotation = NewQuotation();
        var first = AddItem(quotation, productA, 3m, 10_000m);
        var second = AddItem(quotation, productB, 3m, 10_000m);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(
                Product(productA, 10_000m, null, Tiers()),
                Product(productB, 10_000m, null, Tiers())),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        Assert.Equal(5m, DiscountOf(quotation, first));
        Assert.Equal(5m, DiscountOf(quotation, second));

        // 60.000 menos 5% = 57.000, IVA adentro.
        Assert.Equal(57_000m, quotation.Total);
    }

    // Menos de 6 unidades y muy por debajo de los $500.000: la compuerta le quita el descuento a
    // una línea que la escala sí le daba.
    [Fact]
    public async Task BelowBothMinimumsTheDiscountIsTakenAway()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation();
        var item = AddItem(quotation, product, 2m, 10_000m);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(product, 10_000m, null, FlatTier())),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        Assert.Equal(0m, DiscountOf(quotation, item));
        Assert.Equal(20_000m, quotation.Total);
    }

    // Seis unidades bastan por sí solas: la rama de cantidad no mira la plata.
    [Fact]
    public async Task SixUnitsSatisfyTheMinimumOnTheirOwn()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation();
        var item = AddItem(quotation, product, 6m, 1_000m);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(product, 1_000m, null, FlatTier())),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        Assert.Equal(5m, DiscountOf(quotation, item));
        Assert.Equal(5_700m, quotation.Total);
    }

    // Menos de 6 unidades, pero el total ya descontado pasa los $500.000. La compuerta se mide
    // sobre ese total y no sobre el bruto de lista — decisión del owner, 2026-09-21.
    [Fact]
    public async Task TheAmountBranchIsMeasuredOnTheDiscountedTotal()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation();
        var item = AddItem(quotation, product, 2m, 300_000m);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(product, 300_000m, null, FlatTier())),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        // 600.000 menos 5% = 570.000, que sigue por encima de 500.000.
        Assert.Equal(5m, DiscountOf(quotation, item));
        Assert.Equal(570_000m, quotation.Total);
    }

    // El agujero que el owner aceptó explícitamente: $520.000 de lista, 2 unidades. Con el 5% el
    // total candidato queda en 494.000, debajo del mínimo, así que la compuerta lo tumba — y la
    // cotización termina valiendo 520.000, por encima del mínimo que dice no alcanzar.
    [Fact]
    public async Task ADiscountThatWouldSinkTheTotalBelowTheMinimumIsRefused()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation();
        var item = AddItem(quotation, product, 2m, 260_000m);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(product, 260_000m, null, FlatTier())),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        Assert.Equal(0m, DiscountOf(quotation, item));
        Assert.Equal(520_000m, quotation.Total);
    }

    // En dólares el umbral es 200, no 500.000: un literal en pesos dejaría la rama de plata muerta
    // para toda cotización en USD.
    [Fact]
    public async Task TheUsdQuotationUsesTheUsdThreshold()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation(currency: "USD");
        var item = AddItem(quotation, product, 2m, 150m);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(product, null, 150m, FlatTier())),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        // 300 menos 5% = 285, por encima de 200.
        Assert.Equal(5m, DiscountOf(quotation, item));
        Assert.Equal(285m, quotation.Total);
    }

    [Fact]
    public async Task TheUsdQuotationBelowItsThresholdLosesTheDiscount()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation(currency: "USD");
        var item = AddItem(quotation, product, 2m, 50m);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(product, null, 50m, FlatTier())),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        Assert.Equal(0m, DiscountOf(quotation, item));
        Assert.Equal(100m, quotation.Total);
    }

    // Detal: ninguna línea recibe descuento aunque las escalas lo darían. 60 unidades caen en el
    // tramo 49-200 con su 10%, y la compra mínima pasa de sobra — el único motivo por el que la
    // línea queda en cero es el interruptor.
    //
    // Y queda en Own, no en GlobalFloor: una línea con origen de escala y cero por ciento le haría
    // decir a la pantalla "descuento de escala aplicado" sobre nada. Mismo criterio que el barrido
    // de compra mínima.
    [Fact]
    public async Task RetailLeavesEveryLineWithoutDiscount()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation();
        var item = AddItem(quotation, product, 60m, 10_000m);
        quotation.SetIsRetail(true, AdvisorId, Now);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(product, 10_000m, null, Tiers())),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        var line = quotation.Items.Single(candidate => candidate.Id == item);
        Assert.Equal(0m, line.DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.Own, line.DiscountOrigin);
        // El total y no el subtotal: el precio unitario trae el IVA adentro, asi que el subtotal
        // es el neto (600.000 / 1,19). Sin descuento el cliente paga las 60 unidades a lista.
        Assert.Equal(600_000m, quotation.Total);
        Assert.Equal(0m, quotation.DiscountAmount);
    }

    // El corte está arriba del todo: en detal no hay nada que resolver, así que consultar el
    // catálogo sería una lectura por cada guardado del editor sin ningún efecto sobre el
    // resultado.
    [Fact]
    public async Task RetailDoesNotHitTheCatalog()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation();
        AddItem(quotation, product, 60m, 10_000m);
        quotation.SetIsRetail(true, AdvisorId, Now);
        var lookup = new StubPricingLookup(Product(product, 10_000m, null, Tiers()));

        await QuotationPricingRecalculation.ApplyAsync(
            lookup, TenantId, quotation, Now, CancellationToken.None);

        Assert.Equal(0, lookup.ManyCalls);
    }

    // Una cotización sin líneas no tiene nada que resolver y no debe ir al catálogo.
    [Fact]
    public async Task AnEmptyQuotationDoesNotHitTheCatalog()
    {
        var quotation = NewQuotation();
        var lookup = new StubPricingLookup();

        await QuotationPricingRecalculation.ApplyAsync(
            lookup, TenantId, quotation, Now, CancellationToken.None);

        Assert.Equal(0, lookup.ManyCalls);
    }

    private sealed class StubPricingLookup(params QuotationProductPricingRef[] products)
        : IQuotationProductPricingLookup
    {
        private readonly Dictionary<Guid, QuotationProductPricingRef> _products =
            products.ToDictionary(product => product.Id);

        public int ManyCalls { get; private set; }

        public Task<QuotationProductPricingRef?> FindAsync(
            Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
            Task.FromResult(_products.GetValueOrDefault(productId));

        public Task<IReadOnlyDictionary<Guid, QuotationProductPricingRef>> FindManyAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> productIds,
            CancellationToken cancellationToken)
        {
            ManyCalls++;
            return Task.FromResult<IReadOnlyDictionary<Guid, QuotationProductPricingRef>>(
                productIds
                    .Where(_products.ContainsKey)
                    .ToDictionary(id => id, id => _products[id]));
        }
    }
    // Important 1 de la revision: activar el piso global NUNCA puede dejar a la linea peor que
    // antes de activarlo. El spec lo promete explicitamente -- "el global es un piso, no un
    // techo: nadie pierde descuento por activarlo".
    //
    // Sin el arreglo pasaba esto: 5 unidades a 110.000 con su 5% propio dan 522.500, que pasa la
    // compuerta por monto. El piso global de 10% baja el total a 495.000, la compuerta falla, y
    // el barrido deja la linea en 0% -- o sea que pedir el descuento le sube el precio al
    // cliente de 522.500 a 550.000.
    [Fact]
    public async Task TheGlobalFloorNeverLeavesTheLineWorseThanNotUsingItAtAll()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation();
        var itemId = AddItem(quotation, product, 5m, 110_000m);
        quotation.SetGlobalScaleFloor(20, AdvisorId, Now);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(product, 110_000m, null,
            [
                new(5, 9, 5m, QuotationPriceScaleRestriction.Multiple, 1, null, false),
                new(20, 200, 10m, QuotationPriceScaleRestriction.Multiple, 1, null, false)
            ])),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        // Cae al 5% propio, no al 0%: es lo que la cotizacion valia sin el piso global.
        Assert.Equal(5m, DiscountOf(quotation, itemId));
        Assert.Equal(522_500m, quotation.Total);
    }

    // Important 2 de la revision: dos tramos arrancando en el mismo piso, y el de mayor descuento
    // no cumple su restriccion. La linea tiene que caer al otro del mismo piso, no perder el
    // global entero.
    [Fact]
    public async Task TheGlobalFloorFallsBackToAnotherScaleOnTheSameFloorWhenTheBestOneIsBlocked()
    {
        var product = Guid.CreateVersion7();
        var quotation = NewQuotation();
        var itemId = AddItem(quotation, product, 30m, 10_000m);
        quotation.SetGlobalScaleFloor(1000, AdvisorId, Now);

        await QuotationPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(product, 10_000m, null,
            [
                // 30 % 7 != 0 -> bloqueada, aunque sea la de mayor descuento.
                new(1000, 5000, 15m, QuotationPriceScaleRestriction.Multiple, 7, null, false),
                // 30 % 1 == 0 -> esta si aplica, y es el mismo piso que eligio el asesor.
                new(1000, 5000, 12m, QuotationPriceScaleRestriction.Multiple, 1, null, false)
            ])),
            TenantId,
            quotation,
            Now,
            CancellationToken.None);

        Assert.Equal(12m, DiscountOf(quotation, itemId));
    }

}
