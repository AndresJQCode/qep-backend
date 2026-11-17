using BuildingBlocks.Domain.Pricing;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 §5.3, §6 y §10 (P6, P7, P9): un producto de Shopify aplicado a Catalog, con dobles en memoria. El candado, la
/// transacción y el rollback los cubre la integración (<c>ShopifyProductSyncFlowTests</c>); acá, las decisiones.
/// </summary>
public sealed class ShopifyProductSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyVariantSnapshot Variant(long id, string? sku, bool taxable = true) => new(id, sku, null, 10_000m, taxable, []);

    private static ShopifyProductSnapshot Product(
        long id = 7001L,
        ShopifyProductStatus status = ShopifyProductStatus.Active,
        DateTimeOffset? updatedAt = null,
        ShopifyProductImage? image = null,
        string title = "Camiseta",
        params ShopifyVariantSnapshot[] variants) =>
        new(id, title, status, updatedAt ?? Now, [], variants, image);

    [Fact]
    public async Task ANewProductIsCreatedAndLinkedAsImported()
    {
        var world = new SyncWorld();

        var outcome = await world.ApplyAsync(Product(variants: [Variant(1, "NUEVA-1"), Variant(2, null)]));

        Assert.Equal(ShopifyProductOutcomeKind.Created, outcome.Kind);
        Assert.Equal(new ShopifyProductTally(1, 1, 0, 1, 0), outcome.Tally);
        var created = Assert.Single(world.Catalog.Creates);
        Assert.Equal(["NUEVA-1", "shp-2"], created.Variants.Select(variant => variant.Code));
        Assert.All(world.Links.Rows, link => Assert.Equal(ShopifyProductLinkOrigin.Imported, link.Origin));
        Assert.Equal([1L, 2L], world.Links.Rows.Select(link => link.ShopifyVariantId));
        Assert.Contains($"product-lock:{ImporterWorld.ConnectionId}:7001", world.Database.Log);
        Assert.Contains("commit", world.Database.Log);
    }

    [Fact]
    public async Task AnOlderSnapshotThanTheLinkIsUnchanged()
    {
        var world = new SyncWorld();
        world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now);

        var outcome = await world.ApplyAsync(Product(updatedAt: Now.AddMinutes(-1), variants: [Variant(1, "CAM-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Unchanged, outcome.Kind);
        Assert.Empty(world.Catalog.Updates);
        Assert.Empty(world.Catalog.Creates);
    }

    [Fact]
    public async Task AnUpdateWithoutChangesDoesNotTouchTheProduct()
    {
        var world = new SyncWorld();
        world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now.AddDays(-1));
        world.Catalog.Changed = false;

        var outcome = await world.ApplyAsync(Product(variants: [Variant(1, "CAM-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Unchanged, outcome.Kind);
        Assert.Single(world.Catalog.Updates);
        // El vínculo sí anota la versión: un snapshot más viejo que llegue después no pisa.
        Assert.Equal((DateTimeOffset?)Now, Assert.Single(world.Links.Rows).ShopifyUpdatedAt);
    }

    [Fact]
    public async Task AnImportedProductIsUpdatedAndANewVariantIsLinked()
    {
        var world = new SyncWorld();
        var (productId, variants) = world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now.AddDays(-1));

        var outcome = await world.ApplyAsync(Product(title: "Camiseta nueva", variants: [Variant(1, "CAM-1"), Variant(2, "CAM-2")]));

        Assert.Equal(ShopifyProductOutcomeKind.Updated, outcome.Kind);
        var update = Assert.Single(world.Catalog.Updates);
        Assert.Equal((productId, "Camiseta nueva"), (update.ProductId, update.Product.Name));
        Assert.Equal([KeyValuePair.Create(1L, variants[0])], update.Existing.ToArray());
        Assert.Equal([1L, 2L], world.Links.Rows.Select(link => link.ShopifyVariantId).Order());
    }

    [Fact]
    public async Task AVariantRemovedInShopifyLosesItsLink()
    {
        var world = new SyncWorld();
        world.ImportedLinks(7001L, [(1, "CAM-1"), (2, "CAM-2")], updatedAt: Now.AddDays(-1));

        await world.ApplyAsync(Product(variants: [Variant(1, "CAM-1")]));

        Assert.Equal([1L], world.Links.Rows.Select(link => link.ShopifyVariantId));
        Assert.Equal([1L], Assert.Single(world.Catalog.Updates).Existing.Keys);
    }

    [Fact]
    public async Task MatchedBySkuProductsAreNeverWritten()
    {
        var world = new SyncWorld();
        world.MatchedLinks(7001L, (1, "CAM-1"));

        var outcome = await world.ApplyAsync(Product(title: "Otro nombre", variants: [Variant(1, "CAM-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Unchanged, outcome.Kind);
        Assert.Empty(world.Catalog.Updates);
        Assert.Empty(world.Catalog.Creates);
        Assert.Empty(world.Catalog.Activations);
    }

    [Fact]
    public async Task ANewVariantOfAMatchedProductIsLinkedBySku()
    {
        var world = new SyncWorld();
        var (productId, _) = world.MatchedLinks(7001L, (1, "CAM-1"));
        var newVariant = world.QepVariant(productId, "CAM-2");
        world.QepVariant(Guid.CreateVersion7(), "OTRO-1");

        var outcome = await world.ApplyAsync(Product(variants: [Variant(1, "CAM-1"), Variant(2, "CAM-2"), Variant(3, "OTRO-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Matched, outcome.Kind);
        Assert.Equal(new ShopifyProductTally(1, 0, 1, 0, 1), outcome.Tally);
        Assert.Equal([ShopifyProductCodes.SkuInOtherProduct], outcome.IssueCodes);
        var link = Assert.Single(world.Links.Rows, row => row.ShopifyVariantId == 2);
        Assert.Equal((ShopifyProductLinkOrigin.MatchedBySku, newVariant), (link.Origin, link.VariantId));
        Assert.DoesNotContain(world.Links.Rows, row => row.ShopifyVariantId == 3);
    }

    [Fact]
    public async Task AllVariantsMatchingAnExistingProductAreLinkedWithoutCreating()
    {
        var world = new SyncWorld();
        var productId = Guid.CreateVersion7();
        world.QepVariant(productId, "CAM-1");
        world.QepVariant(productId, "CAM-2");

        var outcome = await world.ApplyAsync(Product(variants: [Variant(1, "CAM-1"), Variant(2, "CAM-2"), Variant(3, null)]));

        Assert.Equal(ShopifyProductOutcomeKind.Matched, outcome.Kind);
        Assert.Equal(new ShopifyProductTally(1, 0, 2, 0, 1), outcome.Tally);
        Assert.Equal([ShopifyProductCodes.PartialMatch], outcome.IssueCodes);
        Assert.Empty(world.Catalog.Creates);
        Assert.All(world.Links.Rows, link => Assert.Equal(ShopifyProductLinkOrigin.MatchedBySku, link.Origin));
    }

    [Fact]
    public async Task ArchivedWithoutLinksIsIgnored()
    {
        var world = new SyncWorld();

        var outcome = await world.ApplyAsync(Product(status: ShopifyProductStatus.Archived, variants: [Variant(1, "CAM-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Ignored, outcome.Kind);
        Assert.Empty(world.Catalog.Creates);
        Assert.Empty(world.Links.Rows);
    }

    [Fact]
    public async Task ArchivedWithImportedLinksDeactivates()
    {
        var world = new SyncWorld();
        world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now.AddDays(-1));

        var outcome = await world.ApplyAsync(Product(status: ShopifyProductStatus.Archived, variants: [Variant(1, "CAM-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Deactivated, outcome.Kind);
        Assert.False(Assert.Single(world.Catalog.Updates).Product.IsActive);
    }

    [Fact]
    public async Task ARecreatedProductIsRelinkedAndReactivated()
    {
        var world = new SyncWorld();
        var (productId, variants) = world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now.AddDays(-2), active: false);

        var outcome = await world.ApplyAsync(Product(id: 8001L, variants: [Variant(11, "CAM-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Relinked, outcome.Kind);
        Assert.Equal([(productId, true)], world.Catalog.Activations);
        var link = Assert.Single(world.Links.Rows);
        Assert.Equal((8001L, 11L, variants[0], ShopifyProductLinkOrigin.Imported), (link.ShopifyProductId, link.ShopifyVariantId, link.VariantId, link.Origin));
        Assert.Equal([KeyValuePair.Create(11L, variants[0])], Assert.Single(world.Catalog.Updates).Existing.ToArray());
        // El viejo se consultó en Shopify y ya no existe: por eso se vuelve a vincular.
        Assert.Equal(1, world.Source.ProductRequests);
    }

    // Revisión (P9): el comerciante duplicó el producto en Shopify. El original sigue vivo, así que el duplicado no le quita los
    // vínculos (si lo hiciera, el siguiente webhook del original se los volvería a quitar): se salta con sku_in_other_product.
    [Fact]
    public async Task ADuplicateOfALiveProductDoesNotStealItsLinks()
    {
        var world = new SyncWorld();
        var (_, variants) = world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now.AddDays(-2));
        world.Source.Products[7001L] = Product(id: 7001L, variants: [Variant(1, "CAM-1")]);

        var outcome = await world.ApplyAsync(Product(id: 8001L, variants: [Variant(11, "CAM-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal([ShopifyProductCodes.SkuInOtherProduct], outcome.IssueCodes);
        var link = Assert.Single(world.Links.Rows);
        Assert.Equal((7001L, 1L, variants[0]), (link.ShopifyProductId, link.ShopifyVariantId, link.VariantId));
        Assert.Empty(world.Catalog.Activations);
        Assert.Empty(world.Catalog.Updates);
        Assert.Empty(world.Catalog.Creates);
    }

    // Revisión (P11): la tienda se volvió a enlazar y tiene otra conexión. El mismo producto de Shopify, importado por la conexión
    // vieja del mismo tenant y el mismo dominio, se adopta: sus vínculos pasan a la conexión nueva y no se crea otro producto,
    // aunque no tenga SKU con qué casar.
    [Fact]
    public async Task AReconnectedStoreAdoptsTheProductsOfItsOldConnection()
    {
        var world = new SyncWorld();
        var old = world.OldConnection();
        var (productId, variants) = world.ImportedLinks(7001L, (1, "shp-1"), updatedAt: Now.AddDays(-2), connectionId: old);
        world.Links.Rows[^1].RecordImage(55L, Guid.CreateVersion7(), Now.AddDays(-2));

        var outcome = await world.ApplyAsync(Product(id: 7001L, variants: [Variant(1, null)]));

        Assert.Equal(ShopifyProductOutcomeKind.Updated, outcome.Kind);
        Assert.Empty(world.Catalog.Creates);
        Assert.Equal(productId, Assert.Single(world.Catalog.Updates).ProductId);
        var link = Assert.Single(world.Links.Rows);
        Assert.Equal(
            (ImporterWorld.ConnectionId, 7001L, 1L, variants[0], ShopifyProductLinkOrigin.Imported, (long?)55L),
            (link.ConnectionId, link.ShopifyProductId, link.ShopifyVariantId, link.VariantId, link.Origin, link.ShopifyImageId));
    }

    // P11: un producto recreado en Shopify después de reconectar casa por SKU con uno importado por la conexión vieja: si el viejo
    // ya no existe en Shopify, se vuelve a vincular en la conexión nueva.
    [Fact]
    public async Task ARecreatedProductOfTheOldConnectionIsRelinkedToTheNewOne()
    {
        var world = new SyncWorld();
        var old = world.OldConnection();
        var (productId, variants) = world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now.AddDays(-2), connectionId: old);

        var outcome = await world.ApplyAsync(Product(id: 8001L, variants: [Variant(11, "CAM-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Relinked, outcome.Kind);
        var link = Assert.Single(world.Links.Rows);
        Assert.Equal((ImporterWorld.ConnectionId, 8001L, variants[0]), (link.ConnectionId, link.ShopifyProductId, link.VariantId));
        Assert.Equal(productId, Assert.Single(world.Catalog.Updates).ProductId);
    }

    // Otro tenant con la misma tienda no es «la conexión vieja»: sus vínculos no se tocan.
    [Fact]
    public async Task TheSameShopInAnotherTenantIsNotAdopted()
    {
        var world = new SyncWorld();
        var otherTenant = Guid.CreateVersion7();
        var other = world.OldConnection(tenantId: otherTenant);
        world.Links.Link(otherTenant, other, 7001L, 1L, Guid.CreateVersion7(), Guid.CreateVersion7());

        var outcome = await world.ApplyAsync(Product(id: 7001L, variants: [Variant(1, "NUEVA-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Created, outcome.Kind);
        Assert.Equal(2, world.Links.Rows.Count);
        Assert.Contains(world.Links.Rows, link => link.ConnectionId == other);
    }

    [Fact]
    public async Task ACatalogRejectionSkipsTheWholeProduct()
    {
        var world = new SyncWorld();
        world.Catalog.Rejections.Enqueue("catalog.product.name_too_long");

        var outcome = await world.ApplyAsync(Product(variants: [Variant(1, "A"), Variant(2, "B")]));

        Assert.Equal(ShopifyProductOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(new ShopifyProductTally(1, 0, 0, 0, 2), outcome.Tally);
        Assert.Equal(["catalog.product.name_too_long"], outcome.IssueCodes);
        Assert.Empty(world.Links.Rows);
        Assert.Equal(1, world.UnitOfWork.Discards);
        Assert.DoesNotContain("commit", world.Database.Log);
    }

    [Fact]
    public async Task AnOptionNameRaceIsRetriedOnce()
    {
        var world = new SyncWorld();
        world.Catalog.Rejections.Enqueue(ShopifyProductSync.OptionNameTaken);

        var outcome = await world.ApplyAsync(Product(variants: [Variant(1, "A")]));

        Assert.Equal(ShopifyProductOutcomeKind.Created, outcome.Kind);
        Assert.Equal(2, world.Catalog.CreateAttempts);
    }

    [Fact]
    public async Task ASecondOptionNameRaceIsAConcurrentWrite()
    {
        var world = new SyncWorld();
        world.Catalog.Rejections.Enqueue(ShopifyProductSync.OptionNameTaken);
        world.Catalog.Rejections.Enqueue(ShopifyProductSync.OptionNameTaken);

        var outcome = await world.ApplyAsync(Product(variants: [Variant(1, "A")]));

        Assert.Equal(ShopifyProductOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal([ShopifyProductCodes.ConcurrentWrite], outcome.IssueCodes);
    }

    [Fact]
    public async Task StaleLinksAreDroppedAndTheProductIsCreatedAgain()
    {
        var world = new SyncWorld();
        // Vínculo a una variante que Catalog ya no tiene (el producto se borró a mano).
        world.Links.Link(ImporterWorld.TenantId, ImporterWorld.ConnectionId, 7001L, 1L, Guid.CreateVersion7(), Guid.CreateVersion7(), shopifyUpdatedAt: Now);

        var outcome = await world.ApplyAsync(Product(variants: [Variant(1, "CAM-1")]));

        Assert.Equal(ShopifyProductOutcomeKind.Created, outcome.Kind);
        var link = Assert.Single(world.Links.Rows);
        Assert.Equal(world.Catalog.CreatedProducts.Single(), link.ProductId);
    }

    [Fact]
    public async Task DeleteDeactivatesImportedAndKeepsLinks()
    {
        var world = new SyncWorld();
        var (productId, _) = world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now);

        var outcome = await world.Sync.DeleteAsync(world.Context, 7001L, Ct);

        Assert.Equal(ShopifyProductOutcomeKind.Deactivated, outcome.Kind);
        Assert.Equal([(productId, false)], world.Catalog.Activations);
        Assert.Single(world.Links.Rows);
    }

    [Fact]
    public async Task DeleteOfAMatchedProductIsIgnored()
    {
        var world = new SyncWorld();
        world.MatchedLinks(7001L, (1, "CAM-1"));

        var outcome = await world.Sync.DeleteAsync(world.Context, 7001L, Ct);

        Assert.Equal(ShopifyProductOutcomeKind.Ignored, outcome.Kind);
        Assert.Empty(world.Catalog.Activations);
    }

    [Fact]
    public async Task AFailedImageIsAnIssueAndTheProductStays()
    {
        var world = new SyncWorld();

        var outcome = await world.ApplyAsync(Product(image: new ShopifyProductImage(9, new Uri("https://cdn.shopify.com/a.jpg")), variants: [Variant(1, "A")]));

        Assert.Equal(ShopifyProductOutcomeKind.Created, outcome.Kind);
        Assert.Equal([ShopifyProductCodes.ImageFailed], outcome.IssueCodes);
        Assert.Single(world.Links.Rows);
        Assert.Empty(world.ImageStore.Attached);
    }

    [Fact]
    public async Task TheImageIsAttachedAndRecordedInTheLinks()
    {
        var world = new SyncWorld();
        world.Fetcher.Image = new ShopifyFetchedImage([1, 2, 3], "image/jpeg");

        var outcome = await world.ApplyAsync(Product(image: new ShopifyProductImage(9, new Uri("https://cdn.shopify.com/a.jpg")), variants: [Variant(1, "A")]));

        Assert.Empty(outcome.IssueCodes);
        var attached = Assert.Single(world.ImageStore.Attached);
        Assert.Null(attached.PreviousFileId);
        var link = Assert.Single(world.Links.Rows);
        Assert.Equal(((long?)9L, (Guid?)world.ImageStore.FileId), (link.ShopifyImageId, link.ImageFileId));
    }

    [Fact]
    public async Task TheSameImageIsNotDownloadedAgain()
    {
        var world = new SyncWorld();
        world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now.AddDays(-1), imageId: 9);
        world.Fetcher.Image = new ShopifyFetchedImage([1], "image/jpeg");

        await world.ApplyAsync(Product(image: new ShopifyProductImage(9, new Uri("https://cdn.shopify.com/a.jpg")), variants: [Variant(1, "CAM-1")]));

        Assert.Equal(0, world.Fetcher.Calls);
    }

    [Fact]
    public async Task AChangedImageReplacesThePreviousFile()
    {
        var world = new SyncWorld();
        var previous = Guid.CreateVersion7();
        world.ImportedLinks(7001L, (1, "CAM-1"), updatedAt: Now.AddDays(-1), imageId: 9, imageFileId: previous);
        world.Fetcher.Image = new ShopifyFetchedImage([1], "image/jpeg");

        await world.ApplyAsync(Product(image: new ShopifyProductImage(10, new Uri("https://cdn.shopify.com/b.jpg")), variants: [Variant(1, "CAM-1")]));

        Assert.Equal(previous, Assert.Single(world.ImageStore.Attached).PreviousFileId);
        Assert.Equal((long?)10L, Assert.Single(world.Links.Rows).ShopifyImageId);
    }

    private sealed class SyncWorld
    {
        public SyncWorld()
        {
            UnitOfWork = new FakeUnitOfWork(Database);
            Catalog = new FakeCatalogWriter(Products);
            Context = new ShopifyProductSyncContext(
                new ShopifyConnectionRef(ImporterWorld.TenantId, ImporterWorld.ConnectionId, "1", "mi-tienda.myshopify.com", true, "t"),
                new ShopifyPricingContext(
                    "COP", true, true, new PriceRounding(0, PriceRoundingMode.Nearest), new ShopifyDefaultTaxRates(Guid.CreateVersion7(), Guid.CreateVersion7())),
                Guid.Empty,
                ShopifySyncTrigger.Webhook);
        }

        public FakeShopifyDatabase Database { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; }

        public FakeProductLinks Links { get; } = new();

        public FakeProductLookup Products { get; } = new();

        public FakeCatalogWriter Catalog { get; }

        public FakeImageFetcher Fetcher { get; } = new();

        public FakeImageStore ImageStore { get; } = new();

        public ShopifyProductSyncContext Context { get; }

        public FakeProductsSource Source { get; } = new();

        public ShopifyProductSync Sync =>
            new(UnitOfWork, Links, Catalog, Products, Fetcher, ImageStore, Source, new FakeStoreRepository(Database), new FakeClock(Now));

        /// <summary>La tienda de esta conexión y otra del mismo dominio (la conexión anterior, borrada al volver a enlazar), del
        /// mismo tenant salvo que se pida otro. Devuelve la conexión vieja.</summary>
        public Guid OldConnection(Guid? tenantId = null)
        {
            var current = ShopifyStore.Create(ImporterWorld.ConnectionId, ImporterWorld.TenantId, Now.AddDays(-1));
            current.RecordShopDomain(Context.Connection.ShopDomain);
            Database.Stores[current.ConnectionId] = current;
            var oldConnection = Guid.CreateVersion7();
            var old = ShopifyStore.Create(oldConnection, tenantId ?? ImporterWorld.TenantId, Now.AddDays(-30));
            old.RecordShopDomain(Context.Connection.ShopDomain);
            Database.Stores[oldConnection] = old;
            return oldConnection;
        }

        public Task<ShopifyProductOutcome> ApplyAsync(ShopifyProductSnapshot product) => Sync.ApplyAsync(Context, product, Ct);

        /// <summary>Una variante de QEP con ese código en ese producto.</summary>
        public Guid QepVariant(Guid productId, string code, bool active = true)
        {
            var variantId = Guid.CreateVersion7();
            var reference = new ShopifyProductRef(variantId, productId, code, active, 19);
            Products.Products[code] = reference;
            Products.ByVariant[variantId] = reference;
            return variantId;
        }

        public (Guid ProductId, Guid[] Variants) ImportedLinks(
            long shopifyProductId,
            (long Id, string Sku) variant,
            DateTimeOffset updatedAt,
            bool active = true,
            long? imageId = null,
            Guid? imageFileId = null,
            Guid? connectionId = null) =>
            LinksOf(shopifyProductId, [variant], ShopifyProductLinkOrigin.Imported, updatedAt, active, imageId, imageFileId, connectionId);

        public (Guid ProductId, Guid[] Variants) ImportedLinks(
            long shopifyProductId,
            IReadOnlyList<(long Id, string Sku)> variants,
            DateTimeOffset updatedAt,
            bool active = true,
            long? imageId = null,
            Guid? imageFileId = null) =>
            LinksOf(shopifyProductId, variants, ShopifyProductLinkOrigin.Imported, updatedAt, active, imageId, imageFileId);

        public (Guid ProductId, Guid[] Variants) MatchedLinks(long shopifyProductId, (long Id, string Sku) variant) =>
            LinksOf(shopifyProductId, [variant], ShopifyProductLinkOrigin.MatchedBySku, Now.AddDays(-1), true, null, null);

        private (Guid ProductId, Guid[] Variants) LinksOf(
            long shopifyProductId,
            IReadOnlyList<(long Id, string Sku)> variants,
            ShopifyProductLinkOrigin origin,
            DateTimeOffset updatedAt,
            bool active,
            long? imageId,
            Guid? imageFileId,
            Guid? connectionId = null)
        {
            var productId = Guid.CreateVersion7();
            var ids = new List<Guid>();
            foreach (var (id, sku) in variants)
            {
                var variantId = QepVariant(productId, sku, active);
                ids.Add(variantId);
                Links.Link(
                    ImporterWorld.TenantId, connectionId ?? ImporterWorld.ConnectionId, shopifyProductId, id, productId, variantId, origin, updatedAt);
                Links.Rows[^1].RecordImage(imageId, imageFileId, updatedAt);
            }

            return (productId, [.. ids]);
        }
    }
}

/// <summary>Catalog en memoria: crea ids, anota lo pedido y rechaza con los códigos de <see cref="Rejections"/>. Lo creado entra en
/// el lookup, para que la siguiente pasada lo vea vivo.</summary>
internal sealed class FakeCatalogWriter(FakeProductLookup lookup) : IShopifyCatalogWriter
{
    public Queue<string> Rejections { get; } = new();

    public bool Changed { get; set; } = true;

    public int CreateAttempts { get; private set; }

    public List<ShopifyCatalogProduct> Creates { get; } = [];

    public List<Guid> CreatedProducts { get; } = [];

    public List<(Guid ProductId, ShopifyCatalogProduct Product, IReadOnlyDictionary<long, Guid> Existing)> Updates { get; } = [];

    public List<(Guid ProductId, bool Active)> Activations { get; } = [];

    public List<Guid> Releases { get; } = [];

    public Task<ShopifyCatalogWrite> CreateAsync(ShopifyCatalogProduct product, CancellationToken cancellationToken)
    {
        CreateAttempts++;
        if (Rejections.TryDequeue(out var code))
        {
            return Task.FromResult(ShopifyCatalogWrite.Rejected(code));
        }

        Creates.Add(product);
        var productId = Guid.CreateVersion7();
        CreatedProducts.Add(productId);
        var variants = product.Variants.ToDictionary(variant => variant.ShopifyVariantId, variant => Register(productId, variant.Code));
        return Task.FromResult(new ShopifyCatalogWrite(productId, variants, [], true, null));
    }

    public Task<ShopifyCatalogWrite> UpdateAsync(
        Guid productId, ShopifyCatalogProduct product, IReadOnlyDictionary<long, Guid> existing, CancellationToken cancellationToken)
    {
        if (Rejections.TryDequeue(out var code))
        {
            return Task.FromResult(ShopifyCatalogWrite.Rejected(code));
        }

        Updates.Add((productId, product, existing));
        var variants = product.Variants.ToDictionary(
            variant => variant.ShopifyVariantId,
            variant => existing.TryGetValue(variant.ShopifyVariantId, out var kept) ? kept : Register(productId, variant.Code));
        return Task.FromResult(new ShopifyCatalogWrite(productId, variants, [], Changed, null));
    }

    public Task SetActiveAsync(Guid tenantId, Guid productId, bool active, Guid actorId, CancellationToken cancellationToken)
    {
        Activations.Add((productId, active));
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(Guid tenantId, Guid productId, Guid actorId, CancellationToken cancellationToken)
    {
        Releases.Add(productId);
        return Task.CompletedTask;
    }

    public Task<ShopifyDefaultTaxRates> FindDefaultTaxRatesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new ShopifyDefaultTaxRates(null, null));

    private Guid Register(Guid productId, string code)
    {
        var variantId = Guid.CreateVersion7();
        lookup.ByVariant[variantId] = new ShopifyProductRef(variantId, productId, code, true, 19);
        return variantId;
    }
}

internal sealed class FakeImageFetcher : IShopifyImageFetcher
{
    public ShopifyFetchedImage? Image { get; set; }

    public int Calls { get; private set; }

    public Task<ShopifyFetchedImage?> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Image);
    }
}

internal sealed class FakeImageStore : IShopifyProductImageStore
{
    public Guid FileId { get; } = Guid.CreateVersion7();

    public List<(Guid ProductId, Guid? PreviousFileId)> Attached { get; } = [];

    public Task<ShopifyImageAttach> AttachAsync(
        Guid tenantId, Guid productId, ShopifyFetchedImage image, Guid? previousFileId, Guid actorId, CancellationToken cancellationToken)
    {
        Attached.Add((productId, previousFileId));
        return Task.FromResult(new ShopifyImageAttach(FileId, null));
    }
}
