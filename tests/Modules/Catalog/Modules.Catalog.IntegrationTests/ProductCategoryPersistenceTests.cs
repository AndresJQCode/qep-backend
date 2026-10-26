using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Categorías de producto: el mapeo, el índice único sobre <c>lower(name)</c> con
/// <c>NULLS NOT DISTINCT</c> y la traducción de las dos constraints por nombre. Se prueba por el
/// repositorio y la unidad de trabajo porque, por HTTP, los handlers frenan antes de llegar a la
/// base.
/// </summary>
public sealed class ProductCategoryPersistenceTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-00000000c001");
    private static readonly Guid OtherTenant = Guid.Parse("01900000-0000-7000-8000-00000000c002");
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ACategoryRoundTripsWithItsParentAndStaysInItsTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var root = await SaveCategoryAsync(factory, Tenant, "Bebidas", null);
        var child = await SaveCategoryAsync(factory, Tenant, "Gaseosas", root.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProductCategoryRepository>();
        var tree = await repository.ListTreeAsync(Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(2, tree.Count);
        Assert.Equal(root.Id, tree.Single(category => category.Name == "Gaseosas").ParentId);
        Assert.Null(await repository.FindAsync(OtherTenant, root.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await repository.FindAsync(Tenant, child.Id, TestContext.Current.CancellationToken));
    }

    // Lo que prueba el NULLS NOT DISTINCT: sin él, dos raíces (parent_id NULL) nunca chocan.
    [Fact]
    public async Task TwoRootsWithTheSameNameIgnoringCaseAreNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        await SaveCategoryAsync(factory, Tenant, "Bebidas", null);

        var error = await Assert.ThrowsAsync<CatalogDomainException>(
            () => SaveCategoryAsync(factory, Tenant, "BEBIDAS", null));
        Assert.Equal("catalog.category.name_taken", error.Code);
    }

    [Fact]
    public async Task SiblingsClashButCousinsDoNot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var footwear = await SaveCategoryAsync(factory, Tenant, "Calzado", null);
        var clothes = await SaveCategoryAsync(factory, Tenant, "Ropa", null);
        await SaveCategoryAsync(factory, Tenant, "Accesorios", footwear.Id);

        var error = await Assert.ThrowsAsync<CatalogDomainException>(
            () => SaveCategoryAsync(factory, Tenant, "Accesorios", footwear.Id));
        Assert.Equal("catalog.category.name_taken", error.Code);

        await SaveCategoryAsync(factory, Tenant, "Accesorios", clothes.Id);
    }

    [Fact]
    public async Task ListAsyncCountsDirectProductsAndOnlyActiveOnesWhenAsked()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var drinks = await SaveCategoryAsync(factory, Tenant, "Bebidas", null);
        var sodas = await SaveCategoryAsync(factory, Tenant, "Gaseosas", drinks.Id);
        await SaveProductAsync(factory, NewProduct("X-1", sodas.Id));
        var inactive = NewProduct("X-2", sodas.Id);
        inactive.Deactivate(Now);
        await SaveProductAsync(factory, inactive);
        await SaveProductAsync(factory, NewProduct("X-3", drinks.Id));

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProductCategoryRepository>();

        var all = await repository.ListAsync(Tenant, onlyActiveProducts: false, TestContext.Current.CancellationToken);
        Assert.Equal(["Bebidas", "Gaseosas"], all.Select(item => item.Category.Name));
        Assert.Equal([1, 2], all.Select(item => item.ProductCount));

        var active = await repository.ListAsync(Tenant, onlyActiveProducts: true, TestContext.Current.CancellationToken);
        Assert.Equal([1, 1], active.Select(item => item.ProductCount));
    }

    [Fact]
    public async Task ListIdsWithDescendantsAsyncWalksTheTreeAndIgnoresAForeignRoot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var drinks = await SaveCategoryAsync(factory, Tenant, "Bebidas", null);
        var sodas = await SaveCategoryAsync(factory, Tenant, "Gaseosas", drinks.Id);
        var cola = await SaveCategoryAsync(factory, Tenant, "Cola", sodas.Id);
        var juices = await SaveCategoryAsync(factory, Tenant, "Jugos", drinks.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProductCategoryRepository>();

        var ids = await repository.ListIdsWithDescendantsAsync(
            Tenant, drinks.Id, TestContext.Current.CancellationToken);
        Assert.Equal(
            new[] { drinks.Id, sodas.Id, cola.Id, juices.Id }.Select(id => id.Value).Order(),
            ids.Select(id => id.Value).Order());

        Assert.Empty(await repository.ListIdsWithDescendantsAsync(
            OtherTenant, drinks.Id, TestContext.Current.CancellationToken));
    }

    // Sin la traducción por nombre de la FK, esto sale como DbUpdateException (500).
    [Fact]
    public async Task AProductPointingAtAMissingCategoryIsCategoryNotFoundOnSave()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var error = await Assert.ThrowsAsync<CatalogDomainException>(
            () => SaveProductAsync(factory, NewProduct("X-1", ProductCategoryId.New())));
        Assert.Equal("catalog.product.category_not_found", error.Code);
    }

    // The column round-trips and ListForUpdateAsync tracks: the positions set on its entities are
    // what SaveChanges writes, without touching the version (spec D-5).
    [Fact]
    public async Task QuickAccessPositionsAreSavedThroughTheTrackedListWithoutBumpingTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var drinks = await SaveCategoryAsync(factory, Tenant, "Bebidas", null);
        var shoes = await SaveCategoryAsync(factory, Tenant, "Calzado", null);
        var foreign = await SaveCategoryAsync(factory, OtherTenant, "Ajena", null);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IProductCategoryRepository>();
            var tracked = await repository.ListForUpdateAsync(Tenant, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(tracked, category => category.Id == foreign.Id);
            ProductCategoryQuickAccess.Replace(tracked, [shoes.Id, drinks.Id]);
            await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var tree = await readScope.ServiceProvider.GetRequiredService<IProductCategoryRepository>()
            .ListTreeAsync(Tenant, TestContext.Current.CancellationToken);
        Assert.Equal(
            [("Bebidas", (int?)2, 1L), ("Calzado", 1, 1L)],
            tree.Select(category => (category.Name, category.PosQuickAccessPosition, category.Version)));
    }

    [Fact]
    public async Task CountProductsAsyncCountsDirectActiveAndInactiveProductsOfTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var drinks = await SaveCategoryAsync(factory, Tenant, "Bebidas", null);
        var sodas = await SaveCategoryAsync(factory, Tenant, "Gaseosas", drinks.Id);
        await SaveProductAsync(factory, NewProduct("X-1", drinks.Id));
        var inactive = NewProduct("X-2", drinks.Id);
        inactive.Deactivate(Now);
        await SaveProductAsync(factory, inactive);
        await SaveProductAsync(factory, NewProduct("X-3", sodas.Id));

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProductCategoryRepository>();

        Assert.Equal(2, await repository.CountProductsAsync(Tenant, drinks.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, await repository.CountProductsAsync(Tenant, sodas.Id, TestContext.Current.CancellationToken));
        Assert.Equal(0, await repository.CountProductsAsync(OtherTenant, drinks.Id, TestContext.Current.CancellationToken));
    }

    // The race DeleteCategoryHandler cannot close: a product assigned between its check and the
    // commit. The RESTRICT FK stops it and the unit of work answers the handler's own code, not 500
    // and not the opposite cause (catalog.product.category_not_found).
    [Fact]
    public async Task DeletingACategoryThatAProductUsesIsHasProductsOnSave()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var drinks = await SaveCategoryAsync(factory, Tenant, "Bebidas", null);
        await SaveProductAsync(factory, NewProduct("X-1", drinks.Id));

        var error = await Assert.ThrowsAsync<CatalogDomainException>(
            () => RemoveCategoryAsync(factory, drinks.Id));
        Assert.Equal("catalog.category.has_products", error.Code);
    }

    [Fact]
    public async Task DeletingAParentThatHasAChildIsHasChildrenOnSave()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var drinks = await SaveCategoryAsync(factory, Tenant, "Bebidas", null);
        await SaveCategoryAsync(factory, Tenant, "Gaseosas", drinks.Id);

        var error = await Assert.ThrowsAsync<CatalogDomainException>(
            () => RemoveCategoryAsync(factory, drinks.Id));
        Assert.Equal("catalog.category.has_children", error.Code);
    }

    // The other side of the hierarchy FK: hanging a child from a parent deleted meanwhile.
    [Fact]
    public async Task SavingAChildOfAMissingParentIsParentNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var error = await Assert.ThrowsAsync<CatalogDomainException>(
            () => SaveCategoryAsync(factory, Tenant, "Huérfana", ProductCategoryId.New()));
        Assert.Equal("catalog.category.parent_not_found", error.Code);
    }

    private static async Task RemoveCategoryAsync(QepApiFactory factory, ProductCategoryId categoryId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProductCategoryRepository>();
        var tracked = await repository.ListForUpdateAsync(Tenant, TestContext.Current.CancellationToken);
        repository.Remove(tracked.Single(category => category.Id == categoryId));
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Product NewProduct(string code, ProductCategoryId categoryId) =>
        Product.Create(
            ProductId.New(),
            Tenant,
            $"Producto {code}",
            new ProductDetails { CategoryId = categoryId },
            new ProductVariantInput
            {
                Code = code,
                Pricing = new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["COP"] = 1_000m },
                },
            },
            [],
            Now);

    private static async Task<ProductCategory> SaveCategoryAsync(
        QepApiFactory factory,
        Guid tenantId,
        string name,
        ProductCategoryId? parentId)
    {
        var category = ProductCategory.Create(ProductCategoryId.New(), tenantId, name, parentId, Now);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProductCategoryRepository>().Add(category);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return category;
    }

    private static async Task SaveProductAsync(QepApiFactory factory, Product product)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProductRepository>().Add(product);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
