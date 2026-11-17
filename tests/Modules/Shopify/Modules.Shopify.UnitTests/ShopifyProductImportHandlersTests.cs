using FluentValidation;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.StoreHandlersWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 §5.1, §7 y §10 P4, plan D6: el orden de las guardas del <c>POST …/products/import</c>. El 409 de una importación
/// viva sale del índice al guardar y lo cubre la integración (<c>ShopifyProductsApiTests</c>).
/// </summary>
public sealed class ShopifyProductImportHandlersTests
{
    private const string ReauthorizeUrl = "https://mi-tienda.myshopify.com/admin/oauth/authorize?client_id=x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ImportWithoutCompanyIs422CompanyRequired()
    {
        var bed = new ImportBed(configuredStore: false);

        var exception = await Assert.ThrowsAsync<ShopifyDomainException>(() => bed.StartAsync("All", "https://app.example.co/back"));

        Assert.Equal(ShopifyErrorCodes.ProductsCompanyRequired, exception.Code);
        Assert.Empty(bed.Imports.Rows);
    }

    [Fact]
    public async Task ImportWithoutReadProductsIs422WithReauthorizeUrl()
    {
        var bed = new ImportBed(scopes: ["read_orders", "read_customers"]);

        var exception = await Assert.ThrowsAsync<ShopifyProductsScopeMissingException>(() => bed.StartAsync("All", "https://app.example.co/back"));

        Assert.Equal(ShopifyErrorCodes.ProductsScopeMissing, exception.Code);
        Assert.Equal(ReauthorizeUrl, exception.ProblemExtensions["reauthorizeUrl"]);
        Assert.Equal([("mi-tienda.myshopify.com", "https://app.example.co/back")], bed.Reauthorization.Requests);
        Assert.Empty(bed.Imports.Rows);
    }

    [Fact]
    public async Task ImportWithoutRedirectUriHasANullReauthorizeUrl()
    {
        var bed = new ImportBed(scopes: ["read_orders"]);

        var exception = await Assert.ThrowsAsync<ShopifyProductsScopeMissingException>(() => bed.StartAsync("All", null));

        Assert.True(exception.ProblemExtensions.ContainsKey("reauthorizeUrl"));
        Assert.Null(exception.ProblemExtensions["reauthorizeUrl"]);
        Assert.Empty(bed.Reauthorization.Requests);
    }

    [Fact]
    public async Task WriteProductsCountsAsReadProducts()
    {
        var bed = new ImportBed(scopes: ["read_orders", "write_products"]);

        var started = await bed.StartAsync("missing", null);

        var import = Assert.Single(bed.Imports.Rows);
        Assert.Equal(import.Id, started.ImportId);
        Assert.Equal(ShopifyProductImportMode.Missing, import.Mode);
        Assert.Equal((CallerMembershipId, SubjectId), (import.RequestedBy, import.RequestedBySubjectId));
        var audit = Assert.Single(bed.World.Database.Audit);
        Assert.Equal(ShopifyAuditActions.ProductImportRequested, audit.Action);
    }

    [Fact]
    public async Task AnInvalidModeIs422ValidationFailed()
    {
        var bed = new ImportBed();

        var exception = await Assert.ThrowsAsync<ValidationException>(() => bed.StartAsync("Everything", null));

        Assert.Contains(exception.Errors, error => error.PropertyName == "mode");
        Assert.Empty(bed.Imports.Rows);
    }

    [Fact]
    public async Task ANonOperationalConnectionIs422NotOperational()
    {
        var bed = new ImportBed(operational: false);

        var exception = await Assert.ThrowsAsync<ShopifyDomainException>(() => bed.StartAsync("All", null));

        Assert.Equal(ShopifyErrorCodes.StoreNotOperational, exception.Code);
    }

    private sealed class ImportBed
    {
        public ImportBed(bool configuredStore = true, IReadOnlyCollection<string>? scopes = null, bool operational = true)
        {
            World.Connections.Connections["mi-tienda.myshopify.com"] = World.Connection with
            {
                IsOperational = operational,
                AccessToken = operational ? "shpat_x" : null,
                Scopes = scopes ?? ["read_orders", "read_products"],
            };
            var store = World.SeedStore();
            if (configuredStore)
            {
                store.Configure(
                    AdvisorId,
                    new ShopifyBillingAccount(CompanyId, "Bancolombia", "123-456", "COP"),
                    null,
                    ShopifyStore.DefaultDocumentAttributeKey,
                    ShopifyStore.DefaultDocumentTypeAttributeKey,
                    ShopifyStore.DefaultIdentificationTypeCode,
                    null,
                    ShopifyEditSync.Manual,
                    ShopifyPaymentStatuses.All,
                    AdvisorId,
                    Now);
            }
        }

        public StoreHandlersWorld World { get; } = new();

        public FakeProductImports Imports { get; } = new();

        public FakeReauthorization Reauthorization { get; } = new();

        public Task<ShopifyProductImportStarted> StartAsync(string? mode, string? redirectUri) =>
            new StartShopifyProductImportHandler(
                    World.Context,
                    new ShopifyEnabledModules(),
                    World.Connections,
                    World.Stores,
                    Reauthorization,
                    World.Memberships,
                    Imports,
                    World.UnitOfWork,
                    new FakeAuditRecorder(World.Database),
                    World.Clock,
                    new StartShopifyProductImportValidator())
                .HandleAsync(new StartShopifyProductImportCommand(TenantId, ConnectionId, mode, redirectUri), Ct);
    }

    internal sealed class FakeReauthorization : IShopifyReauthorization
    {
        public List<(string ShopDomain, string RedirectUri)> Requests { get; } = [];

        public Task<string> CreateUrlAsync(Guid tenantId, string shopDomain, string redirectUri, CancellationToken cancellationToken)
        {
            Requests.Add((shopDomain, redirectUri));
            return Task.FromResult(ReauthorizeUrl);
        }
    }
}
