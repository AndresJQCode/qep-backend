using System.Net;
using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// El resumen del reporte de cambios de precio: los agregados que el panel dibuja sin bajarse una
/// sola fila.
///
/// Lo que se prueba aca y no en el handler es la consulta: la direccion del cambio y la serie
/// mensual se resuelven **en la base**, con una expresion que EF tiene que saber traducir. Una
/// prueba unitaria contra un doble no toca ese SQL.
/// </summary>
public sealed class PriceChangeReportSummaryApiTests
{
    /// <summary>Los tres campos del enum, en el orden en el que el contrato los devuelve.</summary>
    private static readonly string[] EveryField =
        ["PriceBaseUsd", "PriceBaseCop", "ScaleDiscount"];

    [Fact]
    public async Task SummaryCountsTheChangesTheirDirectionAndTheProductsTouched()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 90_000m);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<PriceChangeReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);

        Assert.Equal(2, summary.ChangeCount);
        // Dos cambios sobre **un** producto: el conteo de productos es el denominador que dice
        // que no fueron dos productos distintos.
        Assert.Equal(1, summary.ProductCount);
        Assert.Equal(1, summary.IncreaseCount);
        Assert.Equal(1, summary.DecreaseCount);

        var month = Assert.Single(summary.Monthly);
        Assert.Equal(2, month.Count);

        var product = Assert.Single(summary.ByProduct);
        Assert.Equal(productId, product.ProductId);
        Assert.Equal("Vela de soja", product.ProductName);
        Assert.NotNull(product.ProductCode);
        Assert.Equal(1, product.EntityCount);
        Assert.Equal(2, product.Count);

        // Sin rango de fechas no hay ventana anterior contra la cual comparar.
        Assert.Null(summary.Previous);
    }

    /// <summary>
    /// Los tres campos vienen siempre, incluso en cero: una pantalla que recibe solo los campos
    /// con datos tendria que conocer el enum del backend para dibujar el que falta.
    /// </summary>
    [Fact]
    public async Task SummaryAlwaysReturnsTheThreeFieldsEvenWithoutChanges()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var summary = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.ChangeCount);
        Assert.Equal(EveryField, summary.ByField.Select(slice => slice.Field));
        Assert.All(summary.ByField, slice => Assert.Equal(0, slice.Count));
        Assert.Empty(summary.Monthly);
        Assert.Empty(summary.ByProduct);
    }

    /// <summary>El resumen toma exactamente los mismos filtros que el listado: si contara otro
    /// conjunto, el panel y la tabla de la misma pantalla dirian cosas distintas.</summary>
    [Fact]
    public async Task SummaryFiltersByFieldLikeTheList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);

        var cop = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?field=PriceBaseCop",
            TestContext.Current.CancellationToken);
        var usd = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?field=PriceBaseUsd",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, cop?.ChangeCount);
        Assert.Equal(0, usd?.ChangeCount);
    }

    [Fact]
    public async Task SummaryRejectsAFieldThatDoesNotExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?field=FinalPrice",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
