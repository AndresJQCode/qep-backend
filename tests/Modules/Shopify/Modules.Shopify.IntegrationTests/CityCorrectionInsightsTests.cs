using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using CalliShop = Modules.Shopify.IntegrationTests.ShopifyCityCorrectionFlowTests.CalliShop;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-26 D-C3b contra el esquema migrado: <c>CityCorrectionInsights</c> (Bootstrapper) lee <c>quotations</c>,
/// <c>customers</c>, <c>shopify</c>, <c>tenancy</c> e <c>identity</c> por nombre. Renombrar una de esas columnas compila y deja los
/// endpoints de impacto en 500 (42703); estas pruebas resuelven el adaptador real del contenedor para que eso se ponga rojo. Vive en
/// el proyecto de Shopify porque es el que sabe armar pedidos, clientes y huellas reales.
/// </summary>
public sealed class CityCorrectionInsightsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryQueryRunsAgainstTheMigratedSchema()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await using var scope = factory.Services.CreateAsyncScope();
        var insights = scope.ServiceProvider.GetRequiredService<ICityCorrectionInsights>();
        var (tenantId, id) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var synonymId = new CitySynonymId(Guid.CreateVersion7());

        Assert.Empty(await insights.ReadOrdersAsync([new TenantOrderKey(tenantId, id)], Ct));
        Assert.Empty(await insights.ReadCustomersAsync([new TenantCustomerKey(tenantId, id)], Ct));
        Assert.Empty(await insights.ReadCorrectionsAsync(synonymId, Ct));
        Assert.Equal(new SynonymCorrectionCounts(0, 0, 0, 0), Assert.Single(await insights.CountCorrectionsAsync([synonymId], Ct)).Value);
        Assert.Empty(await insights.ReadTenantNamesAsync([id], Ct));
        Assert.Empty(await insights.ReadUserEmailsAsync([id], Ct));
    }

    [Fact]
    public async Task AnOrderOfAnotherTenantIsNotRead()
    {
        await using var shop = await CalliShop.StartAsync();
        await using var scope = shop.Factory.Services.CreateAsyncScope();
        var insights = scope.ServiceProvider.GetRequiredService<ICityCorrectionInsights>();

        Assert.Empty(await insights.ReadOrdersAsync([new TenantOrderKey(Guid.CreateVersion7(), shop.OrderId)], Ct));
        var order = Assert.Single(await insights.ReadOrdersAsync([new TenantOrderKey(shop.World.TenantId, shop.OrderId)], Ct));
        Assert.Equal(
            (shop.World.TenantId, shop.OrderId, "Pending", false, false),
            (order.TenantId, order.OrderId, order.Status, order.HasShippingParty, order.IsStorePickup));
        Assert.False(string.IsNullOrWhiteSpace(order.OrderNumber));
    }

    // El cliente que la importación creó incompleto: el mismo filtro por tenant, y lo que el impacto necesita para decidir si se
    // completa (incompleto, no fusionado, activo, versión 1).
    [Fact]
    public async Task ACustomerOfAnotherTenantIsNotRead()
    {
        await using var shop = await CalliShop.StartAsync();
        await using var scope = shop.Factory.Services.CreateAsyncScope();
        var insights = scope.ServiceProvider.GetRequiredService<ICityCorrectionInsights>();

        Assert.Empty(await insights.ReadCustomersAsync([new TenantCustomerKey(Guid.CreateVersion7(), shop.CustomerId)], Ct));
        Assert.Equal(
            new CustomerInsight(shop.World.TenantId, shop.CustomerId, IsComplete: false, IsMerged: false, IsActive: true, Version: 1),
            Assert.Single(await insights.ReadCustomersAsync([new TenantCustomerKey(shop.World.TenantId, shop.CustomerId)], Ct)));
    }

    [Fact]
    public async Task CountsComeBackInZeroForASynonymWithoutCorrections()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await using var scope = factory.Services.CreateAsyncScope();
        var synonymId = new CitySynonymId(Guid.CreateVersion7());

        var counts = await scope.ServiceProvider.GetRequiredService<ICityCorrectionInsights>().CountCorrectionsAsync([synonymId], Ct);

        Assert.Equal(new SynonymCorrectionCounts(0, 0, 0, 0), counts[synonymId.Value]);
        Assert.Single(counts);
    }

    // O8: lo que corrigió un sinónimo son las huellas de los dos orígenes (la corrección al confirmarlo y lo que casó al
    // importarse). Los trabajos cuentan sólo Pending y Failed: Applied ya está en las huellas y Skipped no se hizo.
    [Fact]
    public async Task CountsTakeBothOriginsAndOnlyPendingAndFailedJobs()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (synonym, other, untouched) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await InsertCorrectionAsync(connectionString, synonym, "Correction", "Order");
        await InsertCorrectionAsync(connectionString, synonym, "Import", "Order");
        await InsertCorrectionAsync(connectionString, synonym, "Import", "Customer");
        await InsertCorrectionAsync(connectionString, other, "Correction", "Order");
        foreach (var status in new[] { "Pending", "Pending", "Failed", "Applied", "Skipped" })
        {
            await InsertJobAsync(connectionString, synonym, status);
        }

        await InsertJobAsync(connectionString, other, "Failed");
        await using var scope = factory.Services.CreateAsyncScope();

        var counts = await scope.ServiceProvider.GetRequiredService<ICityCorrectionInsights>().CountCorrectionsAsync(
            [new CitySynonymId(synonym), new CitySynonymId(other), new CitySynonymId(untouched)], Ct);

        Assert.Equal(new SynonymCorrectionCounts(Orders: 2, Customers: 1, PendingJobs: 2, FailedJobs: 1), counts[synonym]);
        Assert.Equal(new SynonymCorrectionCounts(Orders: 1, Customers: 0, PendingJobs: 0, FailedJobs: 1), counts[other]);
        Assert.Equal(new SynonymCorrectionCounts(0, 0, 0, 0), counts[untouched]);
        Assert.Equal(3, counts.Count);
    }

    // §4.6: «sigue como se escribió» compara la parte de envío campo por campo con la huella. La parte se edita por SQL porque un
    // pedido no edita sus partes (desvío S1).
    [Fact]
    public async Task AShippingPartyEditedBySqlIsNotStillAsWritten()
    {
        await using var shop = await CalliShop.StartAsync();
        await shop.SeedResolveAsync();
        await shop.DrainCorrectionsAsync();
        var synonymId = new CitySynonymId(shop.SynonymId);
        await using var scope = shop.Factory.Services.CreateAsyncScope();
        var insights = scope.ServiceProvider.GetRequiredService<ICityCorrectionInsights>();

        var written = Assert.Single(await insights.ReadCorrectionsAsync(synonymId, Ct));
        Assert.Equal(
            (shop.World.TenantId, "Order", shop.OrderId, shop.CaliId, "Pending", true),
            (written.TenantId, written.Target, written.TargetId, written.CityId, written.OrderStatus, written.StillAsWritten));
        Assert.False(string.IsNullOrWhiteSpace(written.OrderNumber));

        await ExecuteAsync(
            shop.ConnectionString,
            "UPDATE quotations.order_parties SET address = address || ' apto 201' WHERE order_id = @order AND role = 'Shipping'",
            ("order", shop.OrderId));

        Assert.False(Assert.Single(await insights.ReadCorrectionsAsync(synonymId, Ct)).StillAsWritten);
    }

    // La otra mitad: un cliente sigue como se escribió mientras su versión sea la de la huella.
    [Fact]
    public async Task ACustomerEditedAfterTheCorrectionIsNotStillAsWritten()
    {
        await using var shop = await CalliShop.StartAsync();
        await shop.SeedResolveCustomerAsync();
        await shop.DrainCorrectionsAsync();
        var synonymId = new CitySynonymId(shop.SynonymId);
        await using var scope = shop.Factory.Services.CreateAsyncScope();
        var insights = scope.ServiceProvider.GetRequiredService<ICityCorrectionInsights>();

        Assert.Equal(
            new CorrectionInsight(shop.World.TenantId, "Customer", shop.CustomerId, shop.CaliId, null, null, StillAsWritten: true),
            Assert.Single(await insights.ReadCorrectionsAsync(synonymId, Ct)));

        await ExecuteAsync(
            shop.ConnectionString,
            "UPDATE customers.customers SET version = version + 1 WHERE id = @customer",
            ("customer", shop.CustomerId));

        Assert.False(Assert.Single(await insights.ReadCorrectionsAsync(synonymId, Ct)).StillAsWritten);
    }

    private static Task InsertCorrectionAsync(string connectionString, Guid synonymId, string origin, string target) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO shopify.city_corrections (id, tenant_id, synonym_id, origin, target, target_id, department_id, city_id, applied_at, updated_at)
            VALUES (@id, @tenant, @synonym, @origin, @target, @target_id, @department, @city, now(), now())
            """,
            ("id", Guid.CreateVersion7()),
            ("tenant", Guid.CreateVersion7()),
            ("synonym", synonymId),
            ("origin", origin),
            ("target", target),
            ("target_id", Guid.CreateVersion7()),
            ("department", Guid.CreateVersion7()),
            ("city", Guid.CreateVersion7()));

    private static Task InsertJobAsync(string connectionString, Guid synonymId, string status) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO shopify.city_correction_jobs (id, tenant_id, synonym_id, kind, target, target_id, operator_subject_id, status, created_at)
            VALUES (@id, @tenant, @synonym, 'Resolve', 'Order', @target_id, @operator, @status, now())
            """,
            ("id", Guid.CreateVersion7()),
            ("tenant", Guid.CreateVersion7()),
            ("synonym", synonymId),
            ("target_id", Guid.CreateVersion7()),
            ("operator", Guid.CreateVersion7()),
            ("status", status));
}
