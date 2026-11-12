using System.Reflection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>La cotización de la tienda pública: el mismo motor que un pedido (escalas de grupo, detal, mínimo de
/// compra), sin guardar nada. La creación se ejerce contra Postgres en <c>StoreOrderGatewayTests</c> de
/// integración.</summary>
public sealed class StoreOrderGatewayTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid SizeM = Guid.CreateVersion7();
    private static readonly Guid SizeL = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    // 12 a 999 al 10 % y se agrupa entre variantes: 6 + 6 caen las dos en el tramo.
    private static readonly QuotationPriceScaleRef GroupScale = new(12, 999, 10m, QuotationPriceScaleRestriction.Multiple, 1, [], AllowGrouping: true);

    private static readonly IReadOnlyDictionary<string, decimal> Prices =
        new Dictionary<string, decimal> { ["COP"] = 100_000m, ["EUR"] = 25m };

    private static QuotationVariantPricingRef Variant(Guid id, string name, bool active = true) =>
        new(id, ProductId, TenantId, name, name, active, Prices, [GroupScale], 19);

    private static StoreOrderGateway NewGateway(
        QuotationSettings? settings = null, params QuotationVariantPricingRef[] variants)
    {
        var settingsStore = new FixedQuotationSettingsStore(settings);
        var products = new StubQuotationProductLookup(variants.ToDictionary(
            variant => variant.VariantId,
            variant => new QuotationVariantRef(variant.VariantId, variant.ProductId, $"Aceite – {variant.Name}", variant.Code, null, [])));
        var pricing = new StubQuotationProductPricingLookup(variants);
        var rounding = new FixedTenantPriceRounding();
        var drafts = new OrderDraftBuilder(
            ThrowingProxy.Create<IQuotationCustomerLookup>(),
            ThrowingProxy.Create<IQuotationCompanyLookup>(),
            pricing,
            products,
            settingsStore,
            rounding,
            new ShippingResolver(new StubCarrierRepository(), new StubQuotationTaxRateLookup(null), settingsStore),
            new FixedTenantDefaultCurrency(),
            new StubTenantDirectory());
        // Todo lo que escribe o numera es un doble que explota: la cotización no puede tocarlo.
        return new StoreOrderGateway(
            drafts,
            pricing,
            products,
            settingsStore,
            rounding,
            ThrowingProxy.Create<IQuotationCustomerLookup>(),
            ThrowingProxy.Create<IQuotationCompanyLookup>(),
            ThrowingProxy.Create<IOrderRepository>(),
            ThrowingProxy.Create<IQuotationsUnitOfWork>(),
            ThrowingProxy.Create<IQuotationAuditPublisher>(),
            ThrowingProxy.Create<IQuotationLifecycleEventPublisher>(),
            ThrowingProxy.Create<IOrderNumberGenerator>(),
            new DefaultNumberingFormats(),
            new StubTenantDirectory(),
            new FixedTenantClock(Now));
    }

    private static StoreQuoteRequest Request(
        string currency = "COP", bool isRetail = false, params StoreQuoteLine[] lines) =>
        new(TenantId, currency, isRetail, lines.Length == 0 ? [new(SizeM, 6m), new(SizeL, 6m)] : lines);

    [Fact]
    public async Task TheQuoteAppliesGroupDiscountsAcrossLines()
    {
        var gateway = NewGateway(null, Variant(SizeM, "M"), Variant(SizeL, "L"));

        var quote = await gateway.QuoteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(2, quote.Lines.Count);
        Assert.All(quote.Lines, line => Assert.Equal(10m, line.DiscountPercentage));
        Assert.All(quote.Lines, line => Assert.Equal("Group", line.DiscountOrigin));
        Assert.Equal(["Aceite – M", "Aceite – L"], quote.Lines.Select(line => line.Name));
    }

    [Fact]
    public async Task ARetailQuoteHasNoDiscount()
    {
        var gateway = NewGateway(null, Variant(SizeM, "M"), Variant(SizeL, "L"));

        var quote = await gateway.QuoteAsync(Request(isRetail: true), TestContext.Current.CancellationToken);

        Assert.All(quote.Lines, line => Assert.Equal((0m, "Own"), (line.DiscountPercentage, line.DiscountOrigin)));
    }

    [Fact]
    public async Task TheQuoteHonoursTheMinimumPurchaseSweep()
    {
        // 12 unidades no llegan a las 100 que pide el tenant y no hay total mínimo en COP: los descuentos vuelven a 0.
        var settings = QuotationSettings.Create(100, new Dictionary<string, decimal>());
        var gateway = NewGateway(settings, Variant(SizeM, "M"), Variant(SizeL, "L"));

        var quote = await gateway.QuoteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.All(quote.Lines, line => Assert.Equal(0m, line.DiscountPercentage));
    }

    [Fact]
    public async Task TheLineTotalsAddUpToTheTotal()
    {
        var gateway = NewGateway(null, Variant(SizeM, "M"), Variant(SizeL, "L"));

        var quote = await gateway.QuoteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(quote.Total > 0m);
        Assert.Equal(quote.Total, quote.Lines.Sum(line => line.LineTotal));
    }

    [Fact]
    public async Task AQuoteWithAnInactiveVariantThrowsTheEngineCode()
    {
        var gateway = NewGateway(null, Variant(SizeM, "M"), Variant(SizeL, "L", active: false));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            gateway.QuoteAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.item.product_inactive", error.Code);
    }

    [Fact]
    public async Task AQuoteInASecondCurrencyUsesThatPriceList()
    {
        var gateway = NewGateway(null, Variant(SizeM, "M"));

        var quote = await gateway.QuoteAsync(
            Request("EUR", false, new StoreQuoteLine(SizeM, 1m)), TestContext.Current.CancellationToken);

        Assert.Equal("EUR", quote.Currency);
        Assert.Equal(25m, Assert.Single(quote.Lines).UnitPrice);
    }

    [Fact]
    public async Task RepeatedVariantsAreMergedBeforePricing()
    {
        var gateway = NewGateway(null, Variant(SizeM, "M"));

        var quote = await gateway.QuoteAsync(
            Request("COP", false, new StoreQuoteLine(SizeM, 6m), new StoreQuoteLine(SizeM, 6m)),
            TestContext.Current.CancellationToken);

        var line = Assert.Single(quote.Lines);
        Assert.Equal((12m, 10m), (line.Quantity, line.DiscountPercentage));
    }

    // Order.QuoteForStore lleva un número de relleno y un miembro vacío: guardarlo sería corromper el consecutivo.
    // Todos los colaboradores que escriben son proxies que lanzan, así que pasar prueba que no se tocó ninguno.
    [Fact]
    public async Task TheQuoteSavesNothing()
    {
        var gateway = NewGateway(null, Variant(SizeM, "M"), Variant(SizeL, "L"));

        var quote = await gateway.QuoteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(quote.Lines);
    }

    /// <summary>Un doble que lanza en cualquier llamada: afirma por ausencia que un camino no toca el colaborador.</summary>
    // DispatchProxy deriva en runtime de este tipo: no puede ir sellado.
#pragma warning disable CA1852
    private class ThrowingProxy : DispatchProxy
    {
        public static T Create<T>()
            where T : class =>
            Create<T, ThrowingProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"The quote path must not call {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}.");
    }
#pragma warning restore CA1852
}
