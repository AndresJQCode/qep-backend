using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La numeración configurable por tenant, de punta a punta (spec 2026-09-17). El reloj va fijo en la
/// frontera de fin de año de Bogotá: así el año del tenant es 2026 literal en todas las aserciones y
/// ninguna se vuelve intermitente cada 31 de diciembre.
/// </summary>
public sealed class DocumentNumberingApiTests
{
    // Dos tenants, dos formatos, dos contadores. El aislamiento es por la PK de las tres tablas.
    [Fact]
    public async Task TwoTenantsWithDifferentFormatsDoNotInterfere()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);

        var (pwTenantId, _, pwClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = pwClient;
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, pwTenantId, "order", "PW", includeYear: false, "", 1);
        await OrderApiTests.SetOrderCounterAsync(factory, pwTenantId, year: 0, nextValue: 234_235L);
        var pwClientId = await CreateActiveCustomerAsync(pwClient, pwTenantId);
        var pwProductId = await CreateProductWithScalesAsync(pwClient, pwTenantId);

        var (wideTenantId, _, wideClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = wideClient;
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, wideTenantId, "order", "PW-", includeYear: true, "-", 6);
        await OrderApiTests.SetOrderCounterAsync(factory, wideTenantId, year: 2026, nextValue: 7L);
        var wideClientId = await CreateActiveCustomerAsync(wideClient, wideTenantId);
        var wideProductId = await CreateProductWithScalesAsync(wideClient, wideTenantId);

        // Reutiliza el helper de la Task 4: crea su propia cotización enviada y la convierte, así
        // que acá no se duplica la lógica de crear el comprobante de pago y leer el pedido creado.
        var pwNumber = await OrderApiTests.ConvertOneAsync(pwClient, factory, pwTenantId, pwClientId, pwProductId);
        var wideNumber = await OrderApiTests.ConvertOneAsync(
            wideClient, factory, wideTenantId, wideClientId, wideProductId);
        var pwSecond = await OrderApiTests.ConvertOneAsync(pwClient, factory, pwTenantId, pwClientId, pwProductId);

        Assert.Equal("PW234235", pwNumber);
        Assert.Equal("PW-2026-000007", wideNumber);
        // El segundo pedido del tenant PW sigue su propia serie: el otro tenant no la tocó.
        Assert.Equal("PW234236", pwSecond);
    }

    /// <summary>
    /// La prueba de que nada cambia para los que ya están: un tenant sin fila sigue emitiendo
    /// `QUO-2026-…` y `PED-2026-…`, con los cuatro dígitos de siempre.
    /// </summary>
    [Fact]
    public async Task ATenantWithoutARowKeepsTheDefaultNumbersForBothDocuments()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);

        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        // Convierte otra cotización propia: lo que importa acá es el formato por defecto de cada
        // documento, no que sea el pedido de esta cotización puntual.
        var orderNumber = await OrderApiTests.ConvertOneAsync(client, factory, tenantId, clientId, productId);

        Assert.Equal("QUO-2026-0001", quotation.QuotationNumber);
        Assert.Equal("PED-2026-0001", orderNumber);
    }

    /// <summary>
    /// La regla «el consecutivo sólo avanza» (decisión 4 del spec), en una línea: el UPSERT del
    /// runbook con GREATEST. Correrlo dos veces —o con un número menor, que es el error real: copiar
    /// el SQL viejo— no retrocede el contador, porque un número repetido chocaría contra
    /// `IX_orders_tenant_number`.
    /// </summary>
    [Fact]
    public async Task TheRunbookUpsertNeverRewindsTheCounter()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var tenantId = Guid.CreateVersion7();

        await OrderApiTests.SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 234_235L);
        await OrderApiTests.SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 234_235L);
        await OrderApiTests.SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 100L);

        Assert.Equal(234_235L, await OrderCounterAsync(factory, tenantId, year: 0));

        // Y sí avanza cuando el número nuevo es mayor.
        await OrderApiTests.SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 300_000L);

        Assert.Equal(300_000L, await OrderCounterAsync(factory, tenantId, year: 0));
    }

    /// <summary>Lo mismo sobre `quotation_number_counters`: son dos tablas y dos comandos del
    /// runbook, así que el GREATEST se prueba en las dos.</summary>
    [Fact]
    public async Task TheRunbookUpsertNeverRewindsTheQuotationCounter()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var tenantId = Guid.CreateVersion7();

        await QuotationApiTests.SetQuotationCounterAsync(factory, tenantId, year: 0, nextValue: 90_001L);
        await QuotationApiTests.SetQuotationCounterAsync(factory, tenantId, year: 0, nextValue: 5L);

        Assert.Equal(90_001L, await QuotationCounterAsync(factory, tenantId, year: 0));
    }

    private static async Task<long> OrderCounterAsync(QepApiFactory factory, Guid tenantId, int year)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var values = await dbContext.Database
            .SqlQuery<long>(
                $"""
                SELECT next_value AS "Value" FROM quotations.order_number_counters
                WHERE tenant_id = {tenantId} AND year = {year}
                """)
            .ToListAsync(TestContext.Current.CancellationToken);
        return Assert.Single(values);
    }

    private static async Task<long> QuotationCounterAsync(QepApiFactory factory, Guid tenantId, int year)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var values = await dbContext.Database
            .SqlQuery<long>(
                $"""
                SELECT next_value AS "Value" FROM quotations.quotation_number_counters
                WHERE tenant_id = {tenantId} AND year = {year}
                """)
            .ToListAsync(TestContext.Current.CancellationToken);
        return Assert.Single(values);
    }
}
