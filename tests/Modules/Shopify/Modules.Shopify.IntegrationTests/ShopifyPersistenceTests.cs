using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Audit.Domain;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Persistence;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>Spec 2026-10-15 §9 contra Postgres: el esquema <c>shopify</c> nace con la migración del host, los únicos que cierran las
/// carreras existen con el nombre que el código usa, la fila de la tienda y la del espejo vuelven como se guardaron, y la
/// auditoría atómica cae en <c>audit.entries</c> en la misma transacción.</summary>
public sealed class ShopifyPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyOrderMirror NewRow(Guid tenantId, Guid connectionId, long shopifyOrderId = 5_001L) =>
        ShopifyOrderMirror.Create(
            tenantId,
            connectionId,
            shopifyOrderId,
            """{"name":"#1001","lines":[]}""",
            Now,
            new ShopifyOrderHeader("#1001", 7_001L, Now.AddMinutes(-5), null, "pending", "COP", 119_999.50m, false));

    private static Task<string> IndexDefinitionAsync(string connectionString, string index) =>
        ScalarAsync<string>(
            connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'shopify' AND indexname = @index",
            ("index", index));

    [Fact]
    public async Task MigrationsApplyAndUniqueIndexesExist()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();

        var order = await IndexDefinitionAsync(connectionString, "IX_shopify_orders_connection_order");
        var delivery = await IndexDefinitionAsync(connectionString, "IX_webhook_deliveries_webhook_id");

        Assert.Contains("UNIQUE", order, StringComparison.Ordinal);
        Assert.Contains("(connection_id, shopify_order_id)", order, StringComparison.Ordinal);
        Assert.Contains("UNIQUE", delivery, StringComparison.Ordinal);
        Assert.Contains("(webhook_id)", delivery, StringComparison.Ordinal);
        Assert.Contains("shopify_created_at DESC", await IndexDefinitionAsync(connectionString, "IX_shopify_orders_tenant_state_created"), StringComparison.Ordinal);
        Assert.Contains("(tenant_id, qep_order_id)", await IndexDefinitionAsync(connectionString, "IX_shopify_orders_tenant_qep_order"), StringComparison.Ordinal);
        Assert.Contains("(connection_id, shopify_customer_id)", await IndexDefinitionAsync(connectionString, "IX_shopify_orders_connection_customer"), StringComparison.Ordinal);
        Assert.Contains("(tenant_id)", await IndexDefinitionAsync(connectionString, "IX_stores_tenant"), StringComparison.Ordinal);

        // §5.4: las dos columnas que la bandeja necesita para los avisos y para lo que el worker ignoró.
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'shopify' AND table_name = 'orders' AND column_name IN ('last_error', 'warnings_reviewed_at')
            """));
        Assert.Contains("Ignored", await ScalarAsync<string>(
            connectionString,
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'CK_shopify_orders_import_state'"), StringComparison.Ordinal);
        // H19: audit.entries es de Audit; Shopify no crea una tabla propia.
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'shopify' AND table_name LIKE '%audit%'"));
    }

    [Fact]
    public async Task StoreMirrorAndAuditRoundTripInOneSave()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var row = NewRow(tenantId, connectionId);
        row.MarkWaiting(ShopifyWaitReasons.ProductUnmatched, """{"skus":["SH-400"]}""", Now);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>();
            db.Stores.Add(ShopifyStore.Create(connectionId, tenantId, Now));
            db.Orders.Add(row);
            scope.ServiceProvider.GetRequiredService<IShopifyAuditRecorder>().Record(
                tenantId, Guid.CreateVersion7(), AuditActorType.Integration, "shopify.compliance.data_request", "shopify_store",
                connectionId.ToString(), """["shopDomain"]""", Now);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>();
            var store = await db.Stores.AsNoTracking().SingleAsync(value => value.ConnectionId == connectionId, Ct);
            var saved = await db.Orders.AsNoTracking().SingleAsync(value => value.Id == row.Id, Ct);

            Assert.Equal((tenantId, false, "Tipo de documento", 1L), (store.TenantId, store.IsConfigured, store.DocumentTypeAttributeKey, store.Version));
            Assert.Null(store.Billing);
            Assert.Equal((ShopifyImportState.Waiting, "product_unmatched", 1), (saved.ImportState, saved.WaitReason, saved.Attempts));
            Assert.Equal((119_999.50m, "COP", (long?)7_001L), (saved.TotalPrice, saved.Currency, saved.ShopifyCustomerId));
            Assert.Contains("SH-400", saved.WaitDetail, StringComparison.Ordinal);
            Assert.Contains("#1001", saved.Payload, StringComparison.Ordinal);
        }

        Assert.Equal("Waiting", await ScalarAsync<string>(
            connectionString, "SELECT import_state FROM shopify.orders WHERE id = @id", ("id", row.Id)));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @tenantId AND source = 'shopify' AND action = 'shopify.compliance.data_request'",
            ("tenantId", tenantId)));
    }

    // §6.2.1 paso 2: el comprador de Shopify ya visto en el espejo de la misma tienda y el mismo tenant, del pedido más reciente que
    // lo anotó. Una fila sin cliente anotado, otra conexión u otro tenant no cuentan.
    [Fact]
    public async Task MirrorFindsTheLatestCustomerSeenForAShopifyCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var (older, newer) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>();
            db.Orders.AddRange(
                Seen(tenantId, connectionId, 1L, Now.AddDays(-3), older),
                Seen(tenantId, connectionId, 2L, Now.AddDays(-2), newer),
                Seen(tenantId, connectionId, 3L, Now.AddDays(-1), null),
                Seen(tenantId, Guid.CreateVersion7(), 4L, Now, Guid.CreateVersion7()),
                Seen(Guid.CreateVersion7(), connectionId, 5L, Now, Guid.CreateVersion7()));
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var read = factory.Services.CreateScope();
        var mirrors = read.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>();

        Assert.Equal(newer, await mirrors.FindSeenCustomerIdAsync(tenantId, connectionId, 7_001L, Ct));
        Assert.Null(await mirrors.FindSeenCustomerIdAsync(tenantId, connectionId, 7_002L, Ct));
    }

    // §6.1: lo que el PUT de la tienda guarda vuelve igual, cuenta de cobro incluida.
    [Fact]
    public async Task ConfiguredStoreRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var store = ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        var (advisor, classification, company) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        store.Configure(advisor, new ShopifyBillingAccount(company, "Bancolombia", "123-456", "COP"), classification, "Cédula", "Tipo", "NIT", advisor, Now);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IShopifyStoreRepository>().Add(store);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var read = factory.Services.CreateScope();
        var saved = await read.ServiceProvider.GetRequiredService<IShopifyStoreRepository>().FindAsync(store.TenantId, store.ConnectionId, Ct);

        Assert.NotNull(saved);
        Assert.True(saved.IsConfigured);
        Assert.Equal(
            ((Guid?)advisor, new ShopifyBillingAccount(company, "Bancolombia", "123-456", "COP"), (Guid?)classification, "Cédula", "Tipo", "NIT", 2L),
            (saved.AdvisorMembershipId, saved.Billing, saved.DefaultClassificationId, saved.DocumentAttributeKey, saved.DocumentTypeAttributeKey, saved.DefaultIdentificationType, saved.Version));
    }

    private static ShopifyOrderMirror Seen(Guid tenantId, Guid connectionId, long shopifyOrderId, DateTimeOffset createdAt, Guid? qepCustomerId)
    {
        var row = ShopifyOrderMirror.Create(
            tenantId,
            connectionId,
            shopifyOrderId,
            "{}",
            Now,
            new ShopifyOrderHeader($"#{shopifyOrderId}", 7_001L, createdAt, null, "paid", "COP", 1_000m, true));
        if (qepCustomerId is { } id)
        {
            row.AttachCustomer(id);
        }

        return row;
    }

    // §5.4: una fila por pedido y conexión. El webhook y la conciliación a la vez no dejan dos; el perdedor recibe
    // concurrency.conflict (por nombre de índice) y vuelve a leer la fila del ganador.
    [Fact]
    public async Task TheSameOrderTwiceForOneConnectionIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();

        using (var first = factory.Services.CreateScope())
        {
            first.ServiceProvider.GetRequiredService<ShopifyDbContext>().Orders.Add(NewRow(tenantId, connectionId));
            await first.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var second = factory.Services.CreateScope();
        second.ServiceProvider.GetRequiredService<ShopifyDbContext>().Orders.Add(NewRow(tenantId, connectionId));
        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => second.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", error.Code);
    }

    // El worker de webhooks y el reintento manual cargan la misma fila a la vez. El primero la importa;
    // el segundo, que la leyó en espera, no puede devolverla a Waiting: su SaveChanges choca con la versión.
    [Fact]
    public async Task TwoWritersOnTheSameRowTheSecondIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var row = NewRow(Guid.CreateVersion7(), Guid.CreateVersion7());
        using (var seed = factory.Services.CreateScope())
        {
            seed.ServiceProvider.GetRequiredService<ShopifyDbContext>().Orders.Add(row);
            await seed.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var importing = await scopeA.ServiceProvider.GetRequiredService<ShopifyDbContext>().Orders.SingleAsync(value => value.Id == row.Id, Ct);
        var retrying = await scopeB.ServiceProvider.GetRequiredService<ShopifyDbContext>().Orders.SingleAsync(value => value.Id == row.Id, Ct);

        importing.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, Now);
        await scopeA.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);

        retrying.MarkWaiting(ShopifyWaitReasons.ProductUnmatched, null, Now);
        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => scopeB.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal("Imported", await ScalarAsync<string>(
            connectionString, "SELECT import_state FROM shopify.orders WHERE id = @id", ("id", row.Id)));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString, "SELECT version FROM shopify.orders WHERE id = @id", ("id", row.Id)));
    }

    // Spec 2026-10-26 (desvío S3): una huella por sinónimo y pedido o cliente. La importación y el reintento a la vez no dejan dos;
    // el perdedor recibe concurrency.conflict (por nombre de índice) y ApplyAsync relee. La que ganó vuelve como se guardó.
    [Fact]
    public async Task ASecondCorrectionForTheSameTargetIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, synonymId, orderId, mirrorId) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var party = new ShopifyShippingFingerprint("Laura Gómez", "+573001112233", null, "Cra 5 # 10-20", Guid.CreateVersion7(), Guid.CreateVersion7());
        ShopifyCityCorrection NewCorrection() =>
            ShopifyCityCorrection.ForOrder(tenantId, synonymId, ShopifyCityCorrectionOrigin.Import, orderId, mirrorId, party, Now);
        var first = NewCorrection();

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionRepository>().Add(first);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionRepository>()
                .FindAsync(tenantId, synonymId, ShopifyCityCorrectionTarget.Order, orderId, Ct);
            Assert.NotNull(saved);
            Assert.Equal(
                (first.Id, ShopifyCityCorrectionOrigin.Import, (Guid?)mirrorId, party, (long?)null, Now),
                (saved.Id, saved.Origin, saved.MirrorOrderId, saved.Party, saved.CustomerVersion, saved.AppliedAt));
        }

        using var second = factory.Services.CreateScope();
        second.ServiceProvider.GetRequiredService<IShopifyCityCorrectionRepository>().Add(NewCorrection());
        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => second.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal("Order|Import", await ScalarAsync<string>(
            connectionString, "SELECT string_agg(target || '|' || origin, ',') FROM shopify.city_corrections"));
    }

    // Spec 2026-10-26 §4.6 y O8: corregir un sinónimo recorrige todo lo que arregló, de los dos orígenes y en todos los tenants (el
    // sinónimo es de plataforma). Lo de otro sinónimo no entra.
    [Fact]
    public async Task ListBySynonymSeesTheCorrectionsOfEveryTenantAndOrigin()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (synonymId, departmentId, cityId) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var party = new ShopifyShippingFingerprint("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", departmentId, cityId);
        var order = ShopifyCityCorrection.ForOrder(
            Guid.CreateVersion7(), synonymId, ShopifyCityCorrectionOrigin.Correction, Guid.CreateVersion7(), Guid.CreateVersion7(), party, Now);
        var customer = ShopifyCityCorrection.ForCustomer(
            Guid.CreateVersion7(), synonymId, ShopifyCityCorrectionOrigin.Import, Guid.CreateVersion7(), null, departmentId, cityId, 1L, Now);
        var other = ShopifyCityCorrection.ForOrder(
            order.TenantId, Guid.CreateVersion7(), ShopifyCityCorrectionOrigin.Correction, Guid.CreateVersion7(), Guid.CreateVersion7(), party, Now);

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionRepository>();
            repository.Add(order);
            repository.Add(customer);
            repository.Add(other);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var reader = factory.Services.CreateScope();
        var listed = await reader.ServiceProvider.GetRequiredService<IShopifyCityCorrectionRepository>().ListBySynonymAsync(synonymId, Ct);

        Assert.Equal(
            new[] { (order.Id, order.TenantId, ShopifyCityCorrectionOrigin.Correction), (customer.Id, customer.TenantId, ShopifyCityCorrectionOrigin.Import) }
                .OrderBy(entry => entry.Item3),
            listed.Select(correction => (correction.Id, correction.TenantId, correction.Origin)).OrderBy(entry => entry.Item3));
    }

    // §6.1: la tienda nace al primer contacto, y dos a la vez chocan con la PK.
    [Fact]
    public async Task TheStoreCreatedTwiceAtOnceIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();

        using (var first = factory.Services.CreateScope())
        {
            first.ServiceProvider.GetRequiredService<ShopifyDbContext>().Stores.Add(ShopifyStore.Create(connectionId, tenantId, Now));
            await first.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var second = factory.Services.CreateScope();
        second.ServiceProvider.GetRequiredService<ShopifyDbContext>().Stores.Add(ShopifyStore.Create(connectionId, tenantId, Now));
        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => second.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", error.Code);
    }
}
