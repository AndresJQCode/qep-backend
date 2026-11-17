using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
        store.Configure(
            advisor, new ShopifyBillingAccount(company, "Bancolombia", "123-456", "COP"), classification, "Cédula", "Tipo", "NIT", 14,
            ShopifyEditSync.Automatic, ["PendingVerification", "PaymentPending"], advisor, Now);

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
        Assert.Equal(14, saved.HistoryDays);
        Assert.Equal(14, await ScalarAsync<int>(
            database.GetConnectionString(), "SELECT history_days FROM shopify.stores WHERE connection_id = @c", ("c", store.ConnectionId)));
        // Spec 2026-11-14: el modo va por nombre y los estados como text[], en el orden guardado.
        Assert.Equal(ShopifyEditSync.Automatic, saved.EditSync);
        Assert.Equal(["PendingVerification", "PaymentPending"], saved.EditPaymentStatuses);
        Assert.Equal("Automatic|PendingVerification,PaymentPending", await ScalarAsync<string>(
            database.GetConnectionString(),
            "SELECT edit_sync || '|' || array_to_string(edit_payment_statuses, ',') FROM shopify.stores WHERE connection_id = @c",
            ("c", store.ConnectionId)));
    }

    // Spec 2026-11-14: una fila que existía antes de la migración nace Manual y con los cuatro estados (default de la columna).
    [Fact]
    public async Task AStoreRowWithoutEditFieldsGetsTheColumnDefaults()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var store = ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IShopifyStoreRepository>().Add(store);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        await ScalarAsync<int>(
            connectionString,
            """
            WITH reset AS (
                UPDATE shopify.stores SET edit_sync = DEFAULT, edit_payment_statuses = DEFAULT WHERE connection_id = @c RETURNING 1)
            SELECT count(*)::int FROM reset
            """,
            ("c", store.ConnectionId));

        Assert.Equal(
            "Manual|FullPaymentReceived,PartialPaymentReceived,PaymentPending,PendingVerification",
            await ScalarAsync<string>(
                connectionString,
                "SELECT edit_sync || '|' || array_to_string(edit_payment_statuses, ',') FROM shopify.stores WHERE connection_id = @c",
                ("c", store.ConnectionId)));
    }

    // Spec 2026-10-21: el CHECK de la columna repite el rango del dominio; un UPDATE a mano fuera de 1..30 no entra.
    [Fact]
    public async Task HistoryDaysOutOfRangeIsRejectedByTheDatabase()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var store = ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IShopifyStoreRepository>().Add(store);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(
            connectionString, "UPDATE shopify.stores SET history_days = 31 WHERE connection_id = @c", ("c", store.ConnectionId)));

        Assert.Equal(("23514", "CK_stores_history_days"), (error.SqlState, error.ConstraintName));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.stores WHERE history_days IS NOT NULL"));
    }

    // La migración sube y baja: abajo la columna no existe; arriba vuelve, y el host queda en la última para lo que siga.
    [Fact]
    public async Task AddStoreHistoryDaysMigratesDownAndUp()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var migrator = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>()
            .GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        const string ColumnCount =
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'shopify' AND table_name = 'stores' AND column_name = 'history_days'";

        await migrator.MigrateAsync("20261026211954_AddCityCorrectionJobs", Ct);
        var down = await ScalarAsync<long>(connectionString, ColumnCount);
        await migrator.MigrateAsync(null, Ct);
        var up = await ScalarAsync<long>(connectionString, ColumnCount);

        Assert.Equal((0L, 1L), (down, up));
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

    private const string CustomTitle = "Garantía por 1 año + Envío Prioritario🚛";

    private static ShopifyCustomLineMapping NewMapping(Guid tenantId, Guid connectionId, string title, Guid? variantId = null) =>
        ShopifyCustomLineMapping.Create(tenantId, connectionId, title, variantId ?? Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    // Plan 2026-11-17, Tarea 5: una importación viva por tienda, por el índice único parcial y traducida por su nombre.
    [Fact]
    public async Task ASecondLiveImportOfTheStoreIs409ImportRunning()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await SaveImportAsync(factory, NewImport(tenantId, connectionId));

        var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => SaveImportAsync(factory, NewImport(tenantId, connectionId)));

        Assert.Equal(ShopifyErrorCodes.ProductsImportRunning, conflict.Code);
        Assert.Equal("shopify.products.import_running", conflict.Code);
    }

    [Fact]
    public async Task ADoneImportDoesNotBlockANewOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var done = NewImport(tenantId, connectionId);
        done.Start(0);
        done.Complete(Now);
        await SaveImportAsync(factory, done);

        var next = NewImport(tenantId, connectionId);
        await SaveImportAsync(factory, next);

        using var scope = factory.Services.CreateScope();
        var latest = await scope.ServiceProvider.GetRequiredService<IShopifyProductImportRepository>().FindLatestAsync(tenantId, connectionId, Ct);
        Assert.Equal(next.Id, latest!.Id);
    }

    [Fact]
    public async Task TheSameShopifyVariantLinkedTwiceIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await SaveLinkAsync(factory, NewLink(tenantId, connectionId, 10L, 100L));

        var conflict = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => SaveLinkAsync(factory, NewLink(tenantId, connectionId, 10L, 100L)));

        Assert.Equal("concurrency.conflict", conflict.Code);

        using var scope = factory.Services.CreateScope();
        var links = await scope.ServiceProvider.GetRequiredService<IShopifyProductLinkRepository>()
            .FindByShopifyVariantsAsync(tenantId, connectionId, [100L], Ct);
        Assert.Single(links);
    }

    [Fact]
    public async Task ProductsSyncedAtRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var connectionId = Guid.CreateVersion7();
        var store = ShopifyStore.Create(connectionId, Guid.CreateVersion7(), Now);
        store.MarkProductsSynced(Now.AddHours(2));

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ShopifyDbContext>().Stores.Add(store);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var read = factory.Services.CreateScope();
        var saved = await read.ServiceProvider.GetRequiredService<ShopifyDbContext>().Stores.AsNoTracking()
            .SingleAsync(value => value.ConnectionId == connectionId, Ct);
        Assert.Equal(Now.AddHours(2), saved.ProductsSyncedAt);
    }

    [Fact]
    public async Task ImportIssuesRoundTripInCreationOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var import = NewImport(Guid.CreateVersion7(), Guid.CreateVersion7());
        await SaveImportAsync(factory, import);

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IShopifyProductImportRepository>();
            repository.AddIssue(ShopifyProductImportIssue.Create(import.Id, 1L, "Camisa", ShopifyProductCodes.PartialMatch, Now));
            repository.AddIssue(ShopifyProductImportIssue.Create(import.Id, 2L, "Pantalón", ShopifyProductCodes.BarcodeTaken, Now.AddSeconds(1)));
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var read = factory.Services.CreateScope();
        var issues = await read.ServiceProvider.GetRequiredService<IShopifyProductImportRepository>().ListIssuesAsync(import.Id, 50, Ct);
        Assert.Equal(["partial_match", "barcode_taken"], issues.Select(issue => issue.Code));
    }

    private static ShopifyProductImport NewImport(Guid tenantId, Guid connectionId) =>
        ShopifyProductImport.Request(tenantId, connectionId, ShopifyProductImportMode.All, Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    private static ShopifyProductLink NewLink(Guid tenantId, Guid connectionId, long shopifyProductId, long shopifyVariantId) =>
        ShopifyProductLink.Create(
            tenantId, connectionId, shopifyProductId, shopifyVariantId, Guid.CreateVersion7(), Guid.CreateVersion7(),
            ShopifyProductLinkOrigin.Imported, Now, Now);

    private static async Task SaveImportAsync(QepApiFactory factory, ShopifyProductImport import)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IShopifyProductImportRepository>().Add(import);
        await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task SaveLinkAsync(QepApiFactory factory, ShopifyProductLink link)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IShopifyProductLinkRepository>().Add(link);
        await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task SaveAsync(QepApiFactory factory, params ShopifyCustomLineMapping[] mappings)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IShopifyCustomLineMappingRepository>();
        foreach (var mapping in mappings)
        {
            repository.Add(mapping);
        }

        await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
    }

    // Spec 2026-11-14 §2 y §3: la versión aplicada y la proyección en la fila, y el cambio con las columnas del contrato.
    private static async Task<ShopifyOrderEdit> SaveEditAsync(QepApiFactory factory, ShopifyOrderMirror row)
    {
        var edit = ShopifyOrderEdit.Create(
            row.TenantId, row.ConnectionId, row.Id, row.QepOrderId!.Value, Now.AddHours(-1), Now, """{"name":"#1001"}""",
            """[{"kind":"Note","outcome":"Apply","reason":null,"rows":[]}]""", ShopifyOrderEditStatus.Blocked,
            ShopifyEditReasons.OrderNotEditable, Now);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>();
        row.RecordApplied("""{"name":"#1001"}""", """{"buyerNotes":"A"}""");
        db.Orders.Add(row);
        scope.ServiceProvider.GetRequiredService<IShopifyOrderEditRepository>().Add(edit);
        await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        return edit;
    }

    [Fact]
    public async Task OrderEditRoundTripsWithTheContractColumns()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var row = NewRow(Guid.CreateVersion7(), Guid.CreateVersion7());
        row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, Now);
        var edit = await SaveEditAsync(factory, row);

        using (var scope = factory.Services.CreateScope())
        {
            var edits = scope.ServiceProvider.GetRequiredService<IShopifyOrderEditRepository>();
            var open = await edits.FindOpenAsync(row.TenantId, row.Id, Ct);
            Assert.NotNull(open);
            Assert.Equal(
                (edit.Id, ShopifyOrderEditStatus.Blocked, "order_not_editable", row.QepOrderId, (DateTimeOffset?)Now.AddHours(-1), Now),
                (open.Id, open.Status, open.StatusReason, (Guid?)open.QepOrderId, open.BaseUpdatedAt, open.SnapshotUpdatedAt));
            Assert.Equal(1, await edits.CountToReviewAsync(row.TenantId, Ct));
            Assert.Null(await edits.FindOpenAsync(Guid.CreateVersion7(), row.Id, Ct));
            var mirror = await scope.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>()
                .FindByQepOrderIdAsync(row.TenantId, row.QepOrderId!.Value, Ct);
            Assert.Equal(("""{"name": "#1001"}""", """{"buyerNotes": "A"}"""), (mirror?.AppliedPayload, mirror?.AppliedProjection));
        }

        Assert.Equal(
            "id:uuid,tenant_id:uuid,connection_id:uuid,mirror_order_id:uuid,qep_order_id:uuid,base_updated_at:timestamp with time zone,"
            + "snapshot_updated_at:timestamp with time zone,payload:jsonb,groups:jsonb,status:character varying(16),"
            + "status_reason:character varying(64),created_at:timestamp with time zone,decided_at:timestamp with time zone,"
            + "decided_by:uuid,version:bigint",
            await ScalarAsync<string>(
                connectionString,
                """
                SELECT string_agg(a.attname || ':' || format_type(a.atttypid, a.atttypmod), ',' ORDER BY a.attnum)
                FROM pg_attribute a
                WHERE a.attrelid = 'shopify.order_edits'::regclass AND a.attnum > 0 AND NOT a.attisdropped
                """));
        Assert.Contains("(mirror_order_id, status)", await IndexDefinitionAsync(connectionString, "IX_order_edits_mirror_status"), StringComparison.Ordinal);
        Assert.Contains(
            "(tenant_id, status, created_at)",
            await IndexDefinitionAsync(connectionString, "IX_order_edits_tenant_status_created"),
            StringComparison.Ordinal);
        Assert.Equal(
            "applied_payload:jsonb,applied_projection:jsonb,qep_attributes:jsonb",
            await ScalarAsync<string>(
                connectionString,
                """
                SELECT string_agg(a.attname || ':' || format_type(a.atttypid, a.atttypmod), ',' ORDER BY a.attname)
                FROM pg_attribute a
                WHERE a.attrelid = 'shopify.orders'::regclass AND a.attname IN ('applied_payload', 'applied_projection', 'qep_attributes')
                """));
    }

    [Fact]
    public async Task DeletingTheMirrorRowDeletesItsEdits()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var row = NewRow(Guid.CreateVersion7(), Guid.CreateVersion7());
        row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, Now);
        await SaveEditAsync(factory, row);

        await ExecuteAsync(connectionString, "DELETE FROM shopify.orders WHERE id = @id", ("id", row.Id));

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.order_edits"));
    }

    // Contrato con lulo-crm-migrator, que siembra esta tabla por SQL: los nombres y los tipos no se tocan.
    [Fact]
    public async Task CustomLineMappingRoundTripsWithTheContractColumns()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var mapping = NewMapping(Guid.CreateVersion7(), Guid.CreateVersion7(), CustomTitle);

        await SaveAsync(factory, mapping);

        Assert.Equal(
            string.Join('|', mapping.Id, mapping.TenantId, mapping.ConnectionId, CustomTitle, mapping.VariantId, mapping.CreatedBy),
            await ScalarAsync<string>(
                connectionString,
                "SELECT concat_ws('|', id, tenant_id, connection_id, title, variant_id, created_by) FROM shopify.custom_line_mappings"));
        Assert.Equal(
            Now.UtcDateTime,
            await ScalarAsync<DateTime>(connectionString, "SELECT created_at FROM shopify.custom_line_mappings"));
        Assert.Equal(
            "id:uuid,tenant_id:uuid,connection_id:uuid,title:character varying(255),variant_id:uuid,created_at:timestamp with time zone,created_by:uuid",
            await ScalarAsync<string>(
                connectionString,
                """
                SELECT string_agg(a.attname || ':' || format_type(a.atttypid, a.atttypmod), ',' ORDER BY a.attnum)
                FROM pg_attribute a
                WHERE a.attrelid = 'shopify.custom_line_mappings'::regclass AND a.attnum > 0 AND NOT a.attisdropped
                """));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'shopify' AND table_name = 'custom_line_mappings' AND is_nullable = 'YES'"));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connectionString,
                "SELECT count(*) FROM pg_constraint WHERE conrelid = 'shopify.custom_line_mappings'::regclass AND contype = 'f'"));
        Assert.Contains(
            "(connection_id, title)",
            await IndexDefinitionAsync(connectionString, "IX_custom_line_mappings_connection_title"),
            StringComparison.Ordinal);
    }

    // Review Focus 4: el índice único sale como 409 con su código, no como 500 ni como concurrency.conflict.
    [Fact]
    public async Task DuplicateTitleOnTheSameConnectionIsAlreadyMapped()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await SaveAsync(factory, NewMapping(tenantId, connectionId, "Garantía🚛"));

        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => SaveAsync(factory, NewMapping(tenantId, connectionId, " Garantía🚛 ")));

        Assert.Equal("shopify.custom_line.already_mapped", error.Code);

        // Otra mayúscula es otro nombre: la comparación es ordinal.
        await SaveAsync(factory, NewMapping(tenantId, connectionId, "garantía🚛"));
    }

    // Review Focus 5: una asociación nunca aplica fuera de su conexión.
    [Fact]
    public async Task FindVariantIdsByTitlesOnlyReadsItsConnection()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var storeA = Guid.CreateVersion7();
        var storeB = Guid.CreateVersion7();
        var variantA = Guid.CreateVersion7();
        var variantB = Guid.CreateVersion7();
        await SaveAsync(factory, NewMapping(tenantId, storeA, "Garantía🚛", variantA), NewMapping(tenantId, storeB, "Garantía🚛", variantB));

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IShopifyCustomLineMappingRepository>();

        var found = await repository.FindVariantIdsByTitlesAsync(tenantId, storeA, ["Garantía🚛", "Otra"], Ct);
        var otherTenant = await repository.FindVariantIdsByTitlesAsync(Guid.CreateVersion7(), storeA, ["Garantía🚛"], Ct);

        Assert.Equal([KeyValuePair.Create("Garantía🚛", variantA)], found.ToArray());
        Assert.Empty(otherTenant);
    }

    // Minor 3: el repositorio aplica la misma regla de recorte que la entidad; un título crudo de Shopify casa igual.
    [Fact]
    public async Task FindVariantIdsByTitlesTrimsTheRequestedTitles()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        await SaveAsync(factory, NewMapping(tenantId, connectionId, "Garantía🚛", variantId));

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IShopifyCustomLineMappingRepository>();

        var found = await repository.FindVariantIdsByTitlesAsync(tenantId, connectionId, ["  Garantía🚛 ", "   "], Ct);

        Assert.Equal([KeyValuePair.Create("Garantía🚛", variantId)], found.ToArray());
    }

    // Spec 2026-11-17 (onboarding) §5 y §7 H7: el estado del asistente (cobertura con su hora, la aceptación y el resumen del
    // historial) vuelve como se guardó, y guardarlo no sube la versión.
    [Fact]
    public async Task TheSetupStateRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var coverage = new ShopifyProductsCoverage(
            12, 9, false, [new ShopifyCoverageSample("SKU-1", "Camiseta azul"), new ShopifyCoverageSample(null, "Gorra")], Now.AddMinutes(3));
        var summary = new ShopifySetupHistorySummary(
            Now.AddMinutes(10), 7, 40, 31,
            [new ShopifyWaitingCount("product_unmatched", 6), new ShopifyWaitingCount("advisor_inactive", 3)]);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>();
            var store = ShopifyStore.Create(connectionId, tenantId, Now);
            store.RecordProductsCoverage(coverage);
            store.AcceptProductsCoverage(Now.AddMinutes(5));
            store.RecordSetupHistory(summary);
            db.Stores.Add(store);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>();
            var store = await db.Stores.AsNoTracking().SingleAsync(value => value.ConnectionId == connectionId, Ct);

            Assert.NotNull(store.ProductsCoverage);
            Assert.Equal(
                (coverage.ShopifyVariants, coverage.Matched, coverage.CatalogEmpty, coverage.MeasuredAt),
                (store.ProductsCoverage.ShopifyVariants, store.ProductsCoverage.Matched, store.ProductsCoverage.CatalogEmpty, store.ProductsCoverage.MeasuredAt));
            Assert.Equal(coverage.MissingSample, store.ProductsCoverage.MissingSample);
            Assert.Equal(Now.AddMinutes(5), store.ProductsCoverageAcceptedAt);
            Assert.Equal(Now.AddMinutes(10), store.SetupHistoryRanAt);
            Assert.NotNull(store.SetupHistorySummary);
            Assert.Equal(
                (summary.RanAt, summary.Days, summary.Fetched, summary.Imported),
                (store.SetupHistorySummary.RanAt, store.SetupHistorySummary.Days, store.SetupHistorySummary.Fetched, store.SetupHistorySummary.Imported));
            Assert.Equal(summary.Waiting, store.SetupHistorySummary.Waiting);
            Assert.Equal(1L, store.Version);
        }

        // El jsonb lleva la hora de la medición, en camelCase.
        Assert.NotNull(await ScalarAsync<string>(
            connectionString,
            "SELECT products_coverage->>'measuredAt' FROM shopify.stores WHERE connection_id = @id",
            ("id", connectionId)));
    }
}
