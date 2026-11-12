using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationVariantDeletionTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    // 1-9 al 0 %, 10-999 al 5 % y agrupable: 6 + 6 unidades sólo llegan al 5 % juntas.
    private static QuotationPriceScaleRef[] Tiers() =>
    [
        new(1, 9, 0m, QuotationPriceScaleRestriction.Multiple, 1, [], true),
        new(10, 999, 5m, QuotationPriceScaleRestriction.Multiple, 1, [], true)
    ];

    private static QuotationVariantPricingRef Priced(Guid variantId) =>
        new(variantId, variantId, TenantId, "Producto", "SKU", true,
            new Dictionary<string, decimal> { ["COP"] = 10_000m }, Tiers(), 19);

    private static Quotation DraftWith(params (Guid VariantId, decimal Quantity, decimal Discount)[] lines)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", Guid.CreateVersion7(), AdvisorId,
            new DateOnly(2026, 12, 31), "Transferencia bancaria", null, QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "12345678",
                Currency = "COP"
            },
            "COP", customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);

        foreach (var line in lines)
        {
            quotation.AddItem(
                QuotationItemId.New(), line.VariantId, line.VariantId, line.Quantity, 10_000m, line.Discount, 19,
                AdvisorId, Now);
        }

        return quotation;
    }

    private static QuotationVariantRef CatalogVariant(Guid variantId, string code, string name) =>
        new(variantId, Guid.CreateVersion7(), name, code, null, []);

    private static QuotationVariantDeletion NewService(
        IReadOnlyDictionary<Guid, QuotationVariantRef> catalog,
        RecordingDeletedVariantLineStore store,
        PricingLookupThatRefuses pricing,
        CountingUnitOfWork unitOfWork,
        StubQuotationProductLookup? productLookup = null) =>
        new(
            productLookup ?? new StubQuotationProductLookup(catalog),
            store,
            pricing,
            new FixedQuotationSettingsStore(),
            new FixedTenantPriceRounding(),
            unitOfWork);

    [Fact]
    public async Task BackfillUsesTheTrimmedCatalogSnapshot()
    {
        var variantId = Guid.CreateVersion7();
        var longName = new string('N', 205);
        var store = new RecordingDeletedVariantLineStore();
        var service = NewService(
            new Dictionary<Guid, QuotationVariantRef> { [variantId] = CatalogVariant(variantId, "VS-1", longName) },
            store, new PricingLookupThatRefuses(), new CountingUnitOfWork());

        await service.ApplyAsync(TenantId, [variantId], Now, CancellationToken.None);

        var entry = Assert.Single(store.BackfilledSnapshots);
        Assert.Equal(variantId, entry.Key);
        Assert.Equal(new QuotationProductSnapshot("VS-1", longName), entry.Value);
        Assert.Equal(200, entry.Value.Name.Length);
        Assert.EndsWith("…", entry.Value.Name);
    }

    [Fact]
    public async Task DraftsLoseTheLinesAndAreRepricedWithoutAskingForTheDeletedVariant()
    {
        var kept = Guid.CreateVersion7();
        var deleted = Guid.CreateVersion7();
        var draft = DraftWith((kept, 6m, 5m), (deleted, 6m, 5m));
        var versionBefore = draft.Version;
        var store = new RecordingDeletedVariantLineStore { Drafts = [draft] };
        var pricing = new PricingLookupThatRefuses(deleted, Priced(kept));
        var unitOfWork = new CountingUnitOfWork();
        var service = NewService(new Dictionary<Guid, QuotationVariantRef>(), store, pricing, unitOfWork);

        await service.ApplyAsync(TenantId, [deleted], Now, CancellationToken.None);

        var line = Assert.Single(draft.Items);
        Assert.Equal(kept, line.VariantId);
        Assert.Equal(0m, line.DiscountPercentage);
        Assert.Equal(versionBefore + 1, draft.Version);
        Assert.DoesNotContain(deleted, pricing.RequestedVariantIds);
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task WithoutDraftsNothingIsSaved()
    {
        var variantId = Guid.CreateVersion7();
        var store = new RecordingDeletedVariantLineStore();
        var unitOfWork = new CountingUnitOfWork();
        var service = NewService(
            new Dictionary<Guid, QuotationVariantRef> { [variantId] = CatalogVariant(variantId, "VS-1", "Camiseta") },
            store, new PricingLookupThatRefuses(), unitOfWork);

        await service.ApplyAsync(TenantId, [variantId], Now, CancellationToken.None);

        Assert.Equal(0, unitOfWork.Saves);
        Assert.Equal(1, store.BackfillCalls);
    }

    [Fact]
    public async Task AnEmptyVariantListDoesNothing()
    {
        var store = new RecordingDeletedVariantLineStore();
        var lookup = new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>());
        var unitOfWork = new CountingUnitOfWork();
        var service = NewService(
            new Dictionary<Guid, QuotationVariantRef>(), store, new PricingLookupThatRefuses(), unitOfWork, lookup);

        await service.ApplyAsync(TenantId, [], Now, CancellationToken.None);

        Assert.Equal(0, lookup.FindManyCalls);
        Assert.Equal(0, store.BackfillCalls);
        Assert.Equal(0, store.ListCalls);
        Assert.Equal(0, unitOfWork.Saves);
    }

    private sealed class RecordingDeletedVariantLineStore : IDeletedVariantLineStore
    {
        public IReadOnlyList<Quotation> Drafts { get; init; } = [];

        public IReadOnlyDictionary<Guid, QuotationProductSnapshot> BackfilledSnapshots { get; private set; } =
            new Dictionary<Guid, QuotationProductSnapshot>();

        public int BackfillCalls { get; private set; }

        public int ListCalls { get; private set; }

        public Task<int> BackfillProductSnapshotsAsync(
            Guid tenantId,
            IReadOnlyDictionary<Guid, QuotationProductSnapshot> snapshots,
            CancellationToken cancellationToken)
        {
            BackfillCalls++;
            BackfilledSnapshots = snapshots;
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<Quotation>> ListDraftsWithVariantsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(Drafts);
        }
    }

    /// <summary>Falla si le piden la variante borrada: el catálogo ya no la tiene en la transacción real.</summary>
    private sealed class PricingLookupThatRefuses(Guid deletedVariantId, params QuotationVariantPricingRef[] products)
        : IQuotationProductPricingLookup
    {
        private readonly Dictionary<Guid, QuotationVariantPricingRef> _products =
            products.ToDictionary(product => product.VariantId);

        public PricingLookupThatRefuses() : this(Guid.Empty)
        {
        }

        public List<Guid> RequestedVariantIds { get; } = [];

        public Task<QuotationVariantPricingRef?> FindVariantAsync(
            Guid tenantId, Guid variantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("La pasada global usa FindManyAsync.");

        public Task<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
        {
            RequestedVariantIds.AddRange(variantIds);
            if (variantIds.Contains(deletedVariantId))
            {
                throw new InvalidOperationException("Se pidió una variante borrada.");
            }

            return Task.FromResult<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>>(
                variantIds.Where(_products.ContainsKey).ToDictionary(id => id, id => _products[id]));
        }
    }

    private sealed class CountingUnitOfWork : IQuotationsUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(0);
        }

        public Task<IQuotationsTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("El servicio no abre transacciones.");

        public void DiscardPendingChanges()
        {
        }
    }
}
