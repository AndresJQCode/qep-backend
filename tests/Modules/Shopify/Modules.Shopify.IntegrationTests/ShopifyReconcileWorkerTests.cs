using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Workers;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-15 §5.3: el cron de la conciliación de punta a punta, con dos tiendas de tenants distintos. El directorio real
/// (<c>ListOperationalAsync</c>) lee las conexiones de todos los tenants con tracking y con sus tokens; cada tienda tiene que correr
/// en su propio scope —su propio <c>ShopifyDbContext</c>, su propia unidad de trabajo— y nunca en el de la lista ni en el de otra
/// tienda: el importador hace un <c>SaveChanges</c> a mitad de camino que confirmaría lo que otra tienda dejó rastreado. El worker
/// corre a mano (<c>RunOnceAsync</c>).
/// </summary>
public sealed class ShopifyReconcileWorkerTests
{
    private const string ShopA = "tienda-a.myshopify.com";
    private const string ShopB = "tienda-b.myshopify.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorkerTwoStoresOfDifferentTenantsRunEachInItsOwnScope()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin().WithOrder(ShopA, Order(5550000000001)).WithOrder(ShopB, Order(5550000000002));
        var scopes = new ScopeRecorder();
        using var factory = shopify.Factory(connectionString, scopes.Decorate);
        var (tenantA, connectionA) = await LinkedTenantAsync(factory, connectionString, ShopA, "shpat_token_a");
        var (tenantB, connectionB) = await LinkedTenantAsync(factory, connectionString, ShopB, "shpat_token_b");

        await RunOnceAsync(factory);

        // Cada pedido en su tenant y su tienda; las dos tiendas sin configurar, así que esperan.
        Assert.Equal(
            $"{tenantA}|{connectionA}|5550000000001|store_not_configured,{tenantB}|{connectionB}|5550000000002|store_not_configured",
            await ScalarAsync<string>(
                connectionString,
                "SELECT string_agg(tenant_id || '|' || connection_id || '|' || shopify_order_id || '|' || wait_reason, ',' ORDER BY shopify_order_id) FROM shopify.orders"));
        // Cada tienda con su token, nunca el de la otra.
        Assert.All(shopify.Requests, request =>
            Assert.Equal(request.Uri.Host == ShopA ? "shpat_token_a" : "shpat_token_b", request.Token));
        Assert.Contains(shopify.Requests, request => request.Uri.Host == ShopA);
        Assert.Contains(shopify.Requests, request => request.Uri.Host == ShopB);
        // Un scope para la lista y uno por tienda.
        var listScope = Assert.Single(scopes.Listing);
        Assert.Equal([tenantA, tenantB], scopes.Imports.Select(entry => entry.TenantId).Order());
        var storeScopes = scopes.Imports.Select(entry => entry.Scope).Distinct().ToList();
        Assert.Equal(2, storeScopes.Count);
        Assert.DoesNotContain(listScope, storeScopes);
        // Las dos ventanas avanzaron y los dos leases quedaron libres.
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                connectionString, "SELECT count(*) FROM shopify.stores WHERE reconcile_lease_until IS NULL AND last_reconciled_at IS NOT NULL"));
    }

    // Una corrida que ya tiene una tienda (otra réplica) la salta sin tocar su lease; la otra tienda corre igual.
    [Fact]
    public async Task WorkerSkipsTheStoreWhoseLeaseIsHeldAndRunsTheOther()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin().WithOrder(ShopA, Order(5550000000001)).WithOrder(ShopB, Order(5550000000002));
        using var factory = shopify.Factory(connectionString);
        var (_, connectionA) = await LinkedTenantAsync(factory, connectionString, ShopA, "shpat_token_a");
        var (tenantB, _) = await LinkedTenantAsync(factory, connectionString, ShopB, "shpat_token_b");
        await RunOnceAsync(factory);
        shopify.Requests.Clear();
        await ExecuteAsync(
            connectionString, "UPDATE shopify.stores SET reconcile_lease_until = now() + interval '10 minutes' WHERE connection_id = @c",
            ("c", connectionA));

        await RunOnceAsync(factory);

        Assert.DoesNotContain(shopify.Requests, request => request.Uri.Host == ShopA);
        Assert.Contains(shopify.Requests, request => request.Uri.Host == ShopB);
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.stores WHERE connection_id = @c AND reconcile_lease_until > now()", ("c", connectionA)));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.orders WHERE tenant_id = @t", ("t", tenantB)));
    }

    // Ronda 2 de la revisión: un pedido nuevo cuya cabecera el dominio no guarda no tiene fila del espejo y la ventana avanza; el
    // único rastro es el log. Tiene que salir con tenant, conexión, pedido y motivo, sin nada del comprador, y el resumen de la
    // corrida tiene que contarlo.
    [Fact]
    public async Task WorkerLogsANewOrderWithAnInvalidHeaderWithoutBuyerData()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var broken = Order(5550000000009);
        broken["currencyCode"] = "PESOS";
        var shopify = new FakeShopifyAdmin().WithOrder(ShopA, broken);
        var logs = new RecordingLoggerProvider();
        using var factory = shopify.Factory(connectionString, services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
        var (tenantA, connectionA) = await LinkedTenantAsync(factory, connectionString, ShopA, "shpat_token_a");

        await RunOnceAsync(factory);

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.orders"));
        var warning = Assert.Single(logs.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning && entry.Message.Contains("5550000000009", StringComparison.Ordinal)).Message;
        Assert.Contains(tenantA.ToString(), warning, StringComparison.Ordinal);
        Assert.Contains(connectionA.ToString(), warning, StringComparison.Ordinal);
        Assert.Contains("invalid_payload", warning, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("1 skipped", StringComparison.Ordinal));
        foreach (var buyer in new[] { "laura.gomez@example.com", "Gómez", "+573001112233", "1036000111", "Calle 10", "shpat_token_a" })
        {
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(buyer, StringComparison.Ordinal));
        }
    }

    private static JsonObject Order(long id)
    {
        var order = GraphQlFixture("taxes-included");
        order["id"] = $"gid://shopify/Order/{id}";
        return order;
    }

    private static async Task<(Guid TenantId, Guid ConnectionId)> LinkedTenantAsync(
        QepApiFactory factory, string connectionString, string shop, string token)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, tenant.TenantId);
        return (tenant.TenantId, await LinkStoreAsync(factory, tenant.TenantId, tenant.OwnerUserId, shop, token));
    }

    private static async Task RunOnceAsync(QepApiFactory factory)
    {
        var worker = ActivatorUtilities.CreateInstance<ShopifyReconcileWorker>(factory.Services);
        await worker.RunOnceAsync(Ct);
    }

    /// <summary>Envuelve el directorio y el importador registrados por el host (los reales) para anotar en qué scope corrió cada
    /// llamada: el <see cref="IServiceProvider"/> que resolvió el servicio es el del scope.</summary>
    private sealed class ScopeRecorder
    {
        public List<IServiceProvider> Listing { get; } = [];

        public List<(Guid TenantId, IServiceProvider Scope)> Imports { get; } = [];

        public void Decorate(IServiceCollection services)
        {
            var directory = services.Last(descriptor => descriptor.ServiceType == typeof(IShopifyConnectionDirectory));
            var importer = services.Last(descriptor => descriptor.ServiceType == typeof(IShopifyOrderImporter));
            services.AddScoped<IShopifyConnectionDirectory>(provider => new RecordingDirectory(
                (IShopifyConnectionDirectory)ActivatorUtilities.CreateInstance(provider, directory.ImplementationType!), provider, this));
            services.AddScoped<IShopifyOrderImporter>(provider => new RecordingImporter(
                (IShopifyOrderImporter)ActivatorUtilities.CreateInstance(provider, importer.ImplementationType!), provider, this));
        }

        private sealed class RecordingDirectory(IShopifyConnectionDirectory inner, IServiceProvider scope, ScopeRecorder recorder)
            : IShopifyConnectionDirectory
        {
            public Task<ShopifyConnectionRef?> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken) =>
                inner.FindByShopDomainAsync(shopDomain, cancellationToken);

            public Task<IReadOnlyList<ShopifyConnectionRef>> ListOperationalAsync(CancellationToken cancellationToken)
            {
                lock (recorder)
                {
                    recorder.Listing.Add(scope);
                }

                return inner.ListOperationalAsync(cancellationToken);
            }

            public Task<ShopifyConnectionRef?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
                inner.FindAsync(tenantId, connectionId, cancellationToken);
        }

        private sealed class RecordingImporter(IShopifyOrderImporter inner, IServiceProvider scope, ScopeRecorder recorder) : IShopifyOrderImporter
        {
            public Task<ShopifyImportOutcome> ImportAsync(
                ShopifyConnectionRef connection,
                Modules.Shopify.Domain.ShopifyStore store,
                Modules.Shopify.Domain.ShopifyOrderMirror row,
                ShopifyOrderSnapshot snapshot,
                CancellationToken cancellationToken)
            {
                lock (recorder)
                {
                    recorder.Imports.Add((connection.TenantId, scope));
                }

                return inner.ImportAsync(connection, store, row, snapshot, cancellationToken);
            }
        }
    }
}
