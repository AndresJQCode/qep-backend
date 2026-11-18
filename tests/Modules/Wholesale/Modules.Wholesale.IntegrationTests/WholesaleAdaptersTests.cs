using Microsoft.Extensions.DependencyInjection;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>
/// Los adaptadores de Bootstrapper contra Postgres de verdad: lo que las pruebas unitarias sólo pueden fingir (el filtro de
/// precio y de activo, la expansión de categorías, la idempotencia de cliente y proveedor).
/// </summary>
public sealed class WholesaleAdaptersTests(SharedWholesaleHost host) : IClassFixture<SharedWholesaleHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleApiHarness.QepApiFactory Factory => host.Factory;

    // Review Focus 5 (D-10): sin precio en la moneda del mayorista o con el producto inactivo, no se ofrece.
    [Fact]
    public async Task SampleSkipsVariantsWithoutPriceAndInactiveProducts()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var priced = await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "A-1", "Olla", 10_000m);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "A-2", "Tetera", priceCop: null);
        var inactive = await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "A-3", "Sartén", 20_000m);
        await WholesaleApiHarness.ExecuteAsync(
            Factory,
            $"""
            UPDATE catalog.products SET is_active = false
            WHERE id = (SELECT product_id FROM catalog.product_variants WHERE id = {inactive})
            """);

        await using var scope = Factory.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IWholesaleCatalogReader>();
        var (items, _) = await reader.SampleAsync(tenant.TenantId, "COP", null, 20, Ct);

        var item = Assert.Single(items);
        Assert.Equal((priced, "A-1", "Olla", 10_000m), (item.VariantId, item.Code, item.Name, item.BasePrice));
    }

    // D-9: elegir una categoría trae también lo de sus descendientes, y nada de las demás.
    [Fact]
    public async Task SampleIncludesDescendantCategories()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Hogar");
        var kitchen = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Cocina", home);
        var tools = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Ferretería");
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "H-1", "Cojín", 5_000m, home);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "C-1", "Olla", 10_000m, kitchen);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "F-1", "Martillo", 30_000m, tools);

        await using var scope = Factory.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IWholesaleCatalogReader>();
        var (items, total) = await reader.SampleAsync(tenant.TenantId, "COP", [home], 20, Ct);

        Assert.Equal(["Cojín", "Olla"], items.Select(item => item.Name));
        Assert.Equal(2, total);
    }

    // D-7: un cliente que ya tiene ese NIT se reutiliza; dos llamadas dan el mismo cliente.
    [Fact]
    public async Task CustomerProvisioningReusesAnExistingNit()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);

        Guid first;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<IWholesaleCustomerProvisioning>().EnsureCustomerAsync(
                tenant.TenantId, tenant.SubjectId, "Distribuciones Norte SAS", "900123456", "ana@distri.co", Ct);
        }

        Guid second;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            second = await scope.ServiceProvider.GetRequiredService<IWholesaleCustomerProvisioning>().EnsureCustomerAsync(
                tenant.TenantId, tenant.SubjectId, "Distribuciones Norte SAS", "900123456", "ana@distri.co", Ct);
        }

        Assert.Equal(first, second);
        var count = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM customers.customers
            WHERE tenant_id = {tenant.TenantId} AND identification_number = '900123456'
            """);
        Assert.Equal(1, count[0]);
    }

    // D-8: la primera vez crea el proveedor vinculado; la segunda devuelve el mismo, sin otro.
    [Fact]
    public async Task SupplierLinkerCreatesThenReturnsTheSameSupplier()
    {
        var wholesaler = await WholesaleApiHarness.NewTenantAsync(Factory);
        var distributor = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var companyId = await WholesaleApiHarness.CreateCompanyAsync(manager, wholesaler.TenantId, "Mayorista Central SAS");

        WholesaleCompanyRef company;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            company = (await scope.ServiceProvider.GetRequiredService<IWholesaleCompanyLookup>()
                .FindAsync(wholesaler.TenantId, companyId, Ct))!;
        }

        Assert.True(company.Eligible);
        var ids = new List<Guid>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            ids.Add(await scope.ServiceProvider.GetRequiredService<IWholesaleSupplierLinker>().EnsureLinkedSupplierAsync(
                distributor.TenantId, wholesaler.TenantId, "Mayorista Central", company, distributor.SubjectId, Ct));
        }

        Assert.Equal(ids[0], ids[1]);
        var rows = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM suppliers.suppliers
            WHERE tenant_id = {distributor.TenantId} AND linked_tenant_id = {wholesaler.TenantId}
            """);
        Assert.Equal(1, rows[0]);
    }
}
