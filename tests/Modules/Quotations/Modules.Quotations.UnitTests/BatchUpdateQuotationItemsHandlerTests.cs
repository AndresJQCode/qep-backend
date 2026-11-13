using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Cuántas veces la tanda toca el catálogo con precio. El adaptador real no emite SQL con cero ids,
/// así que «sólo bajas no lee» no se ve contando sentencias: sólo se ve en el puerto. El recálculo del
/// final lee siempre las líneas que quedan, y por eso las aserciones miran qué ids se pidieron.
/// </summary>
public sealed class BatchUpdateQuotationItemsHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneReadPricesEveryAddition()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();
        var quotation = QuotationWith();
        var catalog = new CountingPricingCatalog(Product(first), Product(second), Product(third));

        await HandleAsync(
            quotation,
            catalog,
            [new QuotationItemAddition(first, 1m), new QuotationItemAddition(second, 1m), new QuotationItemAddition(third, 1m)],
            []);

        Assert.Equal(3, quotation.Items.Count);
        Assert.Empty(catalog.SingleLookups);
        // La lectura de las altas y la del recálculo: ninguna por línea.
        Assert.Equal(2, catalog.ManyLookups.Count);
        Assert.Equal([first, second, third], catalog.ManyLookups[0]);
    }

    [Fact]
    public async Task RemovalsOnlyReadTheCatalogJustForTheRecalculation()
    {
        var kept = Guid.CreateVersion7();
        var removed = Guid.CreateVersion7();
        var quotation = QuotationWith(kept, removed);
        var catalog = new CountingPricingCatalog(Product(kept), Product(removed));
        var removedItem = quotation.Items.Single(item => item.VariantId == removed);

        await HandleAsync(quotation, catalog, [], [removedItem.Id.Value]);

        Assert.Empty(catalog.SingleLookups);
        Assert.Equal([kept], Assert.Single(catalog.ManyLookups));
    }

    // Traer las variantes juntas no adelanta nada: un itemId que no está sigue rechazando antes que
    // una variante que no existe.
    [Fact]
    public async Task AnUnknownItemRejectsBeforeTheAdditionsAreRead()
    {
        var kept = Guid.CreateVersion7();
        var quotation = QuotationWith(kept);
        var catalog = new CountingPricingCatalog(Product(kept));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => HandleAsync(
            quotation, catalog, [new QuotationItemAddition(Guid.CreateVersion7(), 1m)], [Guid.CreateVersion7()]));

        Assert.Equal("quotation.item.not_found", error.Code);
        Assert.Empty(catalog.SingleLookups);
        Assert.Empty(catalog.ManyLookups);
    }

    private static QuotationVariantPricingRef Product(Guid variantId) =>
        new(variantId, variantId, TenantId, "Vela de soja", "SKU", true,
            new Dictionary<string, decimal> { ["COP"] = 100_000m }, [], null);

    private static Quotation QuotationWith(params Guid[] variantIds)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, Advisor,
            new DateOnly(2026, 11, 30), null, null, QuotationParties.Empty, null,
            "COP", false, [], Advisor, Now);
        foreach (var variantId in variantIds)
        {
            quotation.AddItem(QuotationItemId.New(), variantId, variantId, 1m, 100_000m, 0m, 0, Advisor, Now);
        }

        return quotation;
    }

    private static Task<QuotationDto> HandleAsync(
        Quotation quotation,
        CountingPricingCatalog catalog,
        IReadOnlyList<QuotationItemAddition> toAdd,
        IReadOnlyList<Guid> toRemoveItemIds) =>
        new BatchUpdateQuotationItemsHandler(
            new StubQuotationRepository(quotation),
            new NoOpQuotationsUnitOfWork(),
            new NoOpQuotationAuditPublisher(),
            catalog,
            new FixedQuotationSettingsStore(),
            new FixedTenantPriceRounding(),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            new StubMembershipDirectory(Advisor.Value),
            new StubTenantDirectory(),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now),
            new BatchUpdateQuotationItemsValidator())
            .HandleAsync(
                new BatchUpdateQuotationItemsCommand(TenantId, quotation.Id.Value, toAdd, toRemoveItemIds),
                TestContext.Current.CancellationToken);

    private sealed class CountingPricingCatalog(params QuotationVariantPricingRef[] products)
        : IQuotationProductPricingLookup
    {
        public List<Guid> SingleLookups { get; } = [];

        public List<IReadOnlyCollection<Guid>> ManyLookups { get; } = [];

        public Task<QuotationVariantPricingRef?> FindVariantAsync(
            Guid tenantId, Guid variantId, CancellationToken cancellationToken)
        {
            SingleLookups.Add(variantId);
            return Task.FromResult(products.FirstOrDefault(product => product.VariantId == variantId));
        }

        public Task<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
        {
            ManyLookups.Add(variantIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>>(
                products
                    .Where(product => variantIds.Contains(product.VariantId))
                    .ToDictionary(product => product.VariantId));
        }
    }
}
