using BuildingBlocks.Domain.Pricing;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 §5.2 y §10 (P2, P4): el procesador de una importación reclamada, con dobles en memoria. El reclamo con lease, la
/// transacción por producto y el rollback de un rechazo de Catalog los cubre la integración (<c>ShopifyProductImportFlowTests</c>).
/// </summary>
public sealed class ShopifyProductImportProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyProductSnapshot Product(long id, params long[] variants) =>
        new(
            id,
            $"Producto {id}",
            ShopifyProductStatus.Active,
            Now,
            [],
            [.. variants.Select(variant => new ShopifyVariantSnapshot(variant, $"SKU-{variant}", null, 10_000m, true, []))],
            null);

    [Fact]
    public async Task AnUnsupportedCurrencyFailsBeforeCreatingAnything()
    {
        var world = new ImportWorld();
        world.Source.Shop = new ShopifyShopInfo("ARS", true, 3);
        world.Source.Pages.Enqueue(new ShopifyProductsPage([Product(1, 11)], null));

        await world.ProcessAsync();

        Assert.Equal(ShopifyProductImportStatus.Failed, world.Import.Status);
        Assert.Equal(ShopifyProductCodes.CurrencyUnsupported, world.Import.FailureCode);
        Assert.Empty(world.Sync.Applied);
        Assert.Equal(0, world.Source.PageRequests);
    }

    [Fact]
    public async Task ACompanyRemovedAfterTheRequestFails()
    {
        var world = new ImportWorld(configuredStore: false);
        world.Source.Pages.Enqueue(new ShopifyProductsPage([Product(1, 11)], null));

        await world.ProcessAsync();

        Assert.Equal(ShopifyProductImportStatus.Failed, world.Import.Status);
        Assert.Equal(ShopifyProductCodes.CompanyRequired, world.Import.FailureCode);
        Assert.Empty(world.Sync.Applied);
        Assert.Equal(0, world.Source.ShopRequests);
    }

    [Fact]
    public async Task RejectedIsTokenExpiredAndAccessDeniedIsScopeMissing()
    {
        var rejected = new ImportWorld();
        rejected.Source.Failure = new ShopifyProductsSourceException(ShopifyProductsSourceFailure.Rejected, null);
        await rejected.ProcessAsync();

        var denied = new ImportWorld();
        denied.Source.Failure = new ShopifyProductsSourceException(ShopifyProductsSourceFailure.AccessDenied, null);
        await denied.ProcessAsync();

        Assert.Equal((ShopifyProductImportStatus.Failed, ShopifyProductCodes.TokenExpired), (rejected.Import.Status, rejected.Import.FailureCode));
        Assert.Equal((ShopifyProductImportStatus.Failed, ShopifyProductCodes.ScopeMissing), (denied.Import.Status, denied.Import.FailureCode));
    }

    [Fact]
    public async Task ACurrentConnectionWithoutReadProductsIsScopeMissing()
    {
        var world = new ImportWorld(scopes: ["read_orders"]);

        await world.ProcessAsync();

        Assert.Equal(ShopifyProductCodes.ScopeMissing, world.Import.FailureCode);
        Assert.Equal(0, world.Source.ShopRequests);
    }

    [Fact]
    public async Task ThrottledRetriesKeepingCursorAndCounters()
    {
        var world = new ImportWorld();
        world.Import.Start(20);
        world.Import.RecordPage(new ShopifyProductTally(10, 8, 1, 2, 1), "cursor-1");
        world.Source.Failure = new ShopifyProductsSourceException(ShopifyProductsSourceFailure.Throttled, TimeSpan.FromSeconds(7));

        await world.ProcessAsync(attempts: 1);

        Assert.Equal(ShopifyProductImportStatus.Running, world.Import.Status);
        Assert.Equal("cursor-1", world.Import.Cursor);
        Assert.Equal((20, 10, 8, 1, 2, 1), (world.Import.Total, world.Import.Done, world.Import.Created, world.Import.Matched, world.Import.WithoutSku, world.Import.Skipped));
        Assert.Equal((world.Import.Id, Now.AddSeconds(7)), world.Queue.Renewals[^1]);
        Assert.Equal(["cursor-1"], world.Source.Cursors);
    }

    [Fact]
    public async Task AnUnavailableWithoutRetryAfterWaitsThirtySecondsPerAttempt()
    {
        var world = new ImportWorld();
        world.Source.Failure = new ShopifyProductsSourceException(ShopifyProductsSourceFailure.Unavailable, null);

        await world.ProcessAsync(attempts: 2);

        Assert.Equal(ShopifyProductImportStatus.Running, world.Import.Status);
        Assert.Equal((world.Import.Id, Now.AddSeconds(60)), world.Queue.Renewals[^1]);
    }

    [Fact]
    public async Task TheFourthUnavailableAttemptFailsUnreachable()
    {
        var world = new ImportWorld();
        world.Source.Failure = new ShopifyProductsSourceException(ShopifyProductsSourceFailure.Unavailable, null);

        await world.ProcessAsync(attempts: ShopifyProductImportProcessor.MaxAttempts);

        Assert.Equal(ShopifyProductImportStatus.Failed, world.Import.Status);
        Assert.Equal(ShopifyProductCodes.Unreachable, world.Import.FailureCode);
    }

    [Fact]
    public async Task MissingModeSkipsFullyLinkedProducts()
    {
        var world = new ImportWorld(ShopifyProductImportMode.Missing);
        world.Links.Link(ImporterWorld.TenantId, ImporterWorld.ConnectionId, 1, 11, Guid.CreateVersion7(), Guid.CreateVersion7());
        world.Source.Shop = new ShopifyShopInfo("COP", true, 2);
        world.Source.Pages.Enqueue(new ShopifyProductsPage([Product(1, 11), Product(2, 21)], null));

        await world.ProcessAsync();

        Assert.Equal([2L], world.Sync.Applied.Select(product => product.Id));
        Assert.Equal((2, 2), (world.Import.Total, world.Import.Done));
        Assert.Equal(1, world.Import.Created);
    }

    [Fact]
    public async Task DoneMarksTheStoreAsProductsSynced()
    {
        var world = new ImportWorld();
        world.Source.Shop = new ShopifyShopInfo("COP", false, 3);
        world.Source.Pages.Enqueue(new ShopifyProductsPage([Product(1, 11), Product(2, 21)], "cursor-1"));
        world.Source.Pages.Enqueue(new ShopifyProductsPage([Product(3, 31)], null));
        world.Sync.Outcomes[2] = new ShopifyProductOutcome(
            ShopifyProductOutcomeKind.Skipped, new ShopifyProductTally(1, 0, 0, 0, 1), ["catalog.product.name_too_long"]);

        await world.ProcessAsync();

        Assert.Equal(ShopifyProductImportStatus.Done, world.Import.Status);
        Assert.Equal((3, 3, 2, 1), (world.Import.Total, world.Import.Done, world.Import.Created, world.Import.Skipped));
        Assert.Null(world.Import.Cursor);
        Assert.Equal(Now, world.Store.ProductsSyncedAt);
        var issue = Assert.Single(world.Imports.Issues);
        Assert.Equal((2L, "Producto 2", "catalog.product.name_too_long"), (issue.ShopifyProductId, issue.Title, issue.Code));
        Assert.Equal([null, "cursor-1"], world.Source.Cursors);
        // La importación firma con quien la pidió (P3) y el precio sale de la tienda y la empresa.
        Assert.All(world.Sync.Contexts, context =>
        {
            Assert.Equal(ImportWorld.SubjectId, context.ActorId);
            Assert.Equal(ShopifySyncTrigger.Import, context.Trigger);
            Assert.Equal(("COP", false, true), (context.Pricing.Currency, context.Pricing.TaxesIncluded, context.Pricing.CompanyChargesVat));
        });
    }

    [Fact]
    public async Task AFinishedImportIsLeftAlone()
    {
        var world = new ImportWorld();
        world.Import.Fail(ShopifyProductCodes.TokenExpired, Now);

        await world.ProcessAsync();

        Assert.Equal(0, world.Source.ShopRequests);
        Assert.Empty(world.Queue.Renewals);
    }

    private sealed class ImportWorld
    {
        public static readonly Guid SubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

        public ImportWorld(
            ShopifyProductImportMode mode = ShopifyProductImportMode.All, bool configuredStore = true, IReadOnlyCollection<string>? scopes = null)
        {
            UnitOfWork = new FakeUnitOfWork(Database);
            Catalog = new FakeCatalogWriter(new FakeProductLookup());
            Store = ShopifyStore.Create(ImporterWorld.ConnectionId, ImporterWorld.TenantId, Now.AddDays(-1));
            if (configuredStore)
            {
                Store.Configure(
                    ImporterWorld.AdvisorId,
                    new ShopifyBillingAccount(ImporterWorld.CompanyId, "Bancolombia", "123-456", "COP"),
                    null,
                    ShopifyStore.DefaultDocumentAttributeKey,
                    ShopifyStore.DefaultDocumentTypeAttributeKey,
                    ShopifyStore.DefaultIdentificationTypeCode,
                    null,
                    ShopifyEditSync.Manual,
                    ShopifyPaymentStatuses.All,
                    ImporterWorld.AdvisorId,
                    Now.AddDays(-1));
            }

            Database.Stores[Store.ConnectionId] = Store;
            Connections.Connections["mi-tienda.myshopify.com"] = new ShopifyConnectionRef(
                ImporterWorld.TenantId, ImporterWorld.ConnectionId, "1", "mi-tienda.myshopify.com", true, "shpat_x")
            {
                Scopes = scopes ?? ["read_orders", "read_products"],
            };
            Import = ShopifyProductImport.Request(ImporterWorld.TenantId, ImporterWorld.ConnectionId, mode, ImporterWorld.AdvisorId, SubjectId, Now);
            Imports.Rows.Add(Import);
        }

        public FakeShopifyDatabase Database { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; }

        public FakeCatalogWriter Catalog { get; }

        public FakeConnectionDirectory Connections { get; } = new();

        public FakeBillingAccounts Billing { get; } = new();

        public FakeProductLinks Links { get; } = new();

        public FakeProductImports Imports { get; } = new();

        public FakeProductsSource Source { get; } = new();

        public FakeProductSyncScope Sync { get; } = new();

        public FakeImportQueue Queue { get; } = new();

        public ShopifyStore Store { get; }

        public ShopifyProductImport Import { get; }

        public Task ProcessAsync(int attempts = 1)
        {
            var clock = new FakeClock(Now);
            var pricing = new ShopifyProductPricingResolver(
                new FakeStoreRepository(Database), Billing, Source, Catalog, new FakePriceRounding());
            var processor = new ShopifyProductImportProcessor(
                Imports, Connections, pricing, Source, Links, Sync, Queue, UnitOfWork, clock);
            return processor.ProcessAsync(new ShopifyProductImportClaim(Import.Id, Import.TenantId, attempts), Ct);
        }
    }
}

internal sealed class FakePriceRounding : ITenantPriceRounding
{
    public Task<PriceRounding> GetAsync(Guid tenantId, string currency, CancellationToken cancellationToken) =>
        Task.FromResult(new PriceRounding(0, PriceRoundingMode.Nearest));

    public Task<IReadOnlyDictionary<string, PriceRounding>> GetAllAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>Las importaciones y sus issues en memoria; confirman al instante.</summary>
internal sealed class FakeProductImports : IShopifyProductImportRepository
{
    public List<ShopifyProductImport> Rows { get; } = [];

    public List<ShopifyProductImportIssue> Issues { get; } = [];

    public Task<ShopifyProductImport?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.FirstOrDefault(import => import.Id == id && import.TenantId == tenantId));

    public Task<ShopifyProductImport?> FindLatestAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Rows
            .Where(import => import.TenantId == tenantId && import.ConnectionId == connectionId)
            .OrderByDescending(import => import.CreatedAt)
            .FirstOrDefault());

    public void Add(ShopifyProductImport import) => Rows.Add(import);

    public void AddIssue(ShopifyProductImportIssue issue) => Issues.Add(issue);

    public Task<IReadOnlyList<ShopifyProductImportIssue>> ListIssuesAsync(Guid importId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShopifyProductImportIssue>>([.. Issues.Where(issue => issue.ImportId == importId).Take(limit)]);
}

/// <summary>Shopify en memoria: la tienda, las páginas en orden y, si hay <see cref="Failure"/>, la lanza en toda llamada que no sea
/// <c>ReadShopAsync</c>.</summary>
internal sealed class FakeProductsSource : IShopifyProductsSource
{
    public ShopifyShopInfo Shop { get; set; } = new("COP", true, 1);

    public Queue<ShopifyProductsPage> Pages { get; } = new();

    public Dictionary<long, ShopifyProductSnapshot?> Products { get; } = [];

    public Queue<ShopifyVariantKeysPage> VariantPages { get; } = new();

    public ShopifyProductsSourceException? Failure { get; set; }

    public ShopifyProductsSourceException? ShopFailure { get; set; }

    public int ShopRequests { get; private set; }

    public int PageRequests => Cursors.Count;

    public int ProductRequests { get; private set; }

    public List<string?> Cursors { get; } = [];

    public Task<ShopifyShopInfo> ReadShopAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        ShopRequests++;
        return ShopFailure is { } failure ? Task.FromException<ShopifyShopInfo>(failure) : Task.FromResult(Shop);
    }

    /// <summary>Lo que responde la sugerencia de «¿Cobras impuestos?» (spec 2026-11-17 onboarding §4.3).</summary>
    public bool Taxable { get; set; }

    public int TaxableRequests { get; private set; }

    public Task<bool> HasTaxableVariantAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        TaxableRequests++;
        return ShopFailure is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(Taxable);
    }

    public Task<ShopifyProductsPage> FetchProductsAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken)
    {
        Cursors.Add(cursor);
        if (Failure is { } failure)
        {
            return Task.FromException<ShopifyProductsPage>(failure);
        }

        return Task.FromResult(Pages.TryDequeue(out var page) ? page : new ShopifyProductsPage([], null));
    }

    public Task<ShopifyProductSnapshot?> ReadProductAsync(ShopifyConnectionRef connection, long productId, CancellationToken cancellationToken)
    {
        ProductRequests++;
        if (Failure is { } failure)
        {
            return Task.FromException<ShopifyProductSnapshot?>(failure);
        }

        return Task.FromResult(Products.GetValueOrDefault(productId));
    }

    /// <summary>Cuántas páginas de variantes se pidieron: cada medición de la cobertura pide al menos una.</summary>
    public int VariantKeyRequests { get; private set; }

    public Task<ShopifyVariantKeysPage> ListVariantKeysAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken)
    {
        VariantKeyRequests++;
        if (Failure is { } failure)
        {
            return Task.FromException<ShopifyVariantKeysPage>(failure);
        }

        return Task.FromResult(VariantPages.TryDequeue(out var page) ? page : new ShopifyVariantKeysPage([], null));
    }
}

/// <summary>La sincronización de un producto vista desde quien la llama: anota y devuelve <c>Created</c> salvo lo sembrado en
/// <see cref="Outcomes"/>.</summary>
internal sealed class FakeProductSyncScope : IShopifyProductSyncScope
{
    public List<ShopifyProductSnapshot> Applied { get; } = [];

    public List<long> Deleted { get; } = [];

    public List<ShopifyProductSyncContext> Contexts { get; } = [];

    public Dictionary<long, ShopifyProductOutcome> Outcomes { get; } = [];

    public Task<ShopifyProductOutcome> ApplyAsync(ShopifyProductSyncContext context, ShopifyProductSnapshot product, CancellationToken cancellationToken)
    {
        Applied.Add(product);
        Contexts.Add(context);
        return Task.FromResult(Outcomes.TryGetValue(product.Id, out var outcome)
            ? outcome
            : new ShopifyProductOutcome(ShopifyProductOutcomeKind.Created, new ShopifyProductTally(1, 1, 0, 0, 0), []));
    }

    public Task<ShopifyProductOutcome> DeleteAsync(ShopifyProductSyncContext context, long shopifyProductId, CancellationToken cancellationToken)
    {
        Deleted.Add(shopifyProductId);
        Contexts.Add(context);
        return Task.FromResult(Outcomes.TryGetValue(shopifyProductId, out var outcome)
            ? outcome
            : new ShopifyProductOutcome(ShopifyProductOutcomeKind.Deactivated, new ShopifyProductTally(1, 0, 0, 0, 0), []));
    }
}

internal sealed class FakeImportQueue : IShopifyProductImportQueue
{
    public List<(Guid ImportId, DateTimeOffset Until)> Renewals { get; } = [];

    public List<(Guid ImportId, string Code)> Failures { get; } = [];

    public Task<ShopifyProductImportClaim?> TryClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task RenewAsync(Guid importId, DateTimeOffset until, CancellationToken cancellationToken)
    {
        Renewals.Add((importId, until));
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid importId, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Failures.Add((importId, code));
        return Task.CompletedTask;
    }
}
