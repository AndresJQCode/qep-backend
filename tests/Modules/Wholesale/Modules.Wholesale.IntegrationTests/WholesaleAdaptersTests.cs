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
        var (items, _) = await reader.SampleAsync(tenant.TenantId, "COP", null, null, null, 20, Ct);

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
        var (items, total) = await reader.SampleAsync(tenant.TenantId, "COP", [home], null, null, 20, Ct);

        Assert.Equal(["Cojín", "Olla"], items.Select(item => item.Name));
        Assert.Equal(2, total);
    }

    // Fase 3: lo que el distribuidor puede pedir por id. Una variante sin precio en la moneda, de un producto inactivo o de
    // otro tenant no vuelve; la que vuelve trae su camino de categorías (la suya y sus ancestros).
    [Fact]
    public async Task FindSellableSkipsInactiveAndUnpricedVariants()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        var other = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        using var otherManager = WholesaleApiHarness.CreateManager(Factory, other);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Hogar");
        var kitchen = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Cocina", home);
        var priced = await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "A-1", "Olla", 10_000m, kitchen);
        var unpriced = await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "A-2", "Tetera", priceCop: null);
        var inactive = await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "A-3", "Sartén", 20_000m);
        var foreign = await WholesaleApiHarness.CreateVariantAsync(otherManager, other.TenantId, "A-4", "Ajena", 5_000m);
        await WholesaleApiHarness.ExecuteAsync(
            Factory,
            $"""
            UPDATE catalog.products SET is_active = false
            WHERE id = (SELECT product_id FROM catalog.product_variants WHERE id = {inactive})
            """);

        await using var scope = Factory.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IWholesaleCatalogReader>();
        var found = await reader.FindSellableAsync(tenant.TenantId, "COP", [priced, unpriced, inactive, foreign], Ct);

        var item = Assert.Single(found.Values);
        Assert.Equal((priced, "A-1", "Olla", 10_000m), (item.VariantId, item.Code, item.Name, item.BasePrice));
        Assert.Equal([kitchen, home], item.CategoryPath);
    }

    // Fase 3: la búsqueda del catálogo del mayorista expande las categorías a sus descendientes y filtra por código o nombre.
    [Fact]
    public async Task SearchSellableFiltersByCategoryAndText()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Hogar");
        var kitchen = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Cocina", home);
        var tools = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Ferretería");
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "H-1", "Cojín", 5_000m, home);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "C-1", "Olla", 10_000m, kitchen);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "F-1", "Olla de hierro", 30_000m, tools);

        await using var scope = Factory.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IWholesaleCatalogReader>();
        var (byCategory, categoryTotal) = await reader.SearchSellableAsync(
            tenant.TenantId, "COP", [home], null, null, null, 1, 50, Ct);
        var (byText, textTotal) = await reader.SearchSellableAsync(tenant.TenantId, "COP", [home], null, null, "olla", 1, 50, Ct);
        var (byCode, _) = await reader.SearchSellableAsync(tenant.TenantId, "COP", null, null, null, "F-1", 1, 50, Ct);

        Assert.Equal(["Cojín", "Olla"], byCategory.Select(item => item.Name));
        Assert.Equal(2, categoryTotal);
        Assert.Equal(["Olla"], byText.Select(item => item.Name));
        Assert.Equal(1, textTotal);
        Assert.Equal(["Olla de hierro"], byCode.Select(item => item.Name));
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
                tenant.TenantId, tenant.SubjectId, "Distribuciones Norte SAS", "CO", "NIT", "900123456", "ana@distri.co", null, null, null, null, "Ana Pérez", Ct);
        }

        Guid second;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            second = await scope.ServiceProvider.GetRequiredService<IWholesaleCustomerProvisioning>().EnsureCustomerAsync(
                tenant.TenantId, tenant.SubjectId, "Distribuciones Norte SAS", "CO", "NIT", "900123456", "ana@distri.co", null, null, null, null, "Ana Pérez", Ct);
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

    // Spec 2026-11-18 (país y documento): el cliente nace con el tipo del distribuidor, y el mismo número con otro tipo es otro cliente.
    [Fact]
    public async Task CustomerProvisioningUsesTheGivenTypeAndReusesOnlyTheSameType()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);

        async Task<Guid> EnsureAsync(string type)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IWholesaleCustomerProvisioning>().EnsureCustomerAsync(
                tenant.TenantId, tenant.SubjectId, "Distribuciones Norte SAS", "CO", type, "123456", "ana@distri.co", null, null, null, null, "Ana Pérez", Ct);
        }

        var ce = await EnsureAsync("CE");
        var ceAgain = await EnsureAsync("CE");
        var cc = await EnsureAsync("CC");

        Assert.Equal(ce, ceAgain);
        Assert.NotEqual(ce, cc);
        var types = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT identification_type AS "Value" FROM customers.customers
            WHERE tenant_id = {tenant.TenantId} AND identification_number = '123456' ORDER BY identification_type
            """);
        Assert.Equal(2, types.Count);
    }

    // Un distribuidor de afuera: el cliente nace con su país y su tipo, no como colombiano.
    [Fact]
    public async Task CustomerProvisioningCreatesAForeignCustomerWithItsCountry()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);

        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWholesaleCustomerProvisioning>().EnsureCustomerAsync(
            tenant.TenantId, tenant.SubjectId, "Distri Madrid SL", "ES", "NIF", "B12345678", "ana@distri.es", null, null, null, null, "Ana Pérez", Ct);

        var rows = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT country AS "Value" FROM customers.customers
            WHERE tenant_id = {tenant.TenantId} AND identification_number = 'B12345678'
            """);
        Assert.Equal(["ES"], rows);
    }

    // Spec 2026-11-23 §4: el cliente del mayorista nace con el celular del aviso del distribuidor.
    [Fact]
    public async Task TheCustomerIsBornWithThePhone()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);

        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWholesaleCustomerProvisioning>().EnsureCustomerAsync(
            tenant.TenantId, tenant.SubjectId, "Distribuciones Norte SAS", "CO", "NIT", "900123456", "ana@distri.co",
            "+573001234567", null, null, null, "Ana Pérez", Ct);

        var rows = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT phone_e164 AS "Value" FROM customers.customers
            WHERE tenant_id = {tenant.TenantId} AND identification_number = '900123456'
            """);
        Assert.Equal(["+573001234567"], rows);
    }

    private async Task<Guid> EnsureCustomerAsync(
        WholesaleTestTenant tenant, string identification, string? phone, Guid? cityId, string? address, string contact)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWholesaleCustomerProvisioning>().EnsureCustomerAsync(
            tenant.TenantId, tenant.SubjectId, "Distribuciones Norte SAS", "CO", "NIT", identification, "ana@distri.co", phone,
            cityId, null, address, contact, Ct);
    }

    private async Task<T> ScalarAsync<T>(FormattableString sql) =>
        Assert.Single(await WholesaleApiHarness.RowsAsync<T>(Factory, sql));

    // Decisión del owner 2026-11-24: con teléfono y ciudad el cliente nace completo, con su ciudad y su dirección, y el
    // contacto del distribuidor es la etiqueta de la dirección principal de la libreta.
    [Fact]
    public async Task TheCustomerIsCompleteWithCityAddressContactLabelAndPhone()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var medellin = await WholesaleApiHarness.MedellinAsync(manager);

        var id = await EnsureCustomerAsync(tenant, "900123456", "+573001234567", medellin, "Calle 10 # 40-20", "Ana Pérez");

        Assert.True(await ScalarAsync<bool>($"""SELECT (completeness = 'Complete') AS "Value" FROM customers.customers WHERE id = {id}"""));
        Assert.Equal(medellin, await ScalarAsync<Guid>($"""SELECT city_id AS "Value" FROM customers.customers WHERE id = {id}"""));
        Assert.Equal(
            "Calle 10 # 40-20", await ScalarAsync<string>($"""SELECT address AS "Value" FROM customers.customers WHERE id = {id}"""));
        Assert.Equal(
            "+573001234567", await ScalarAsync<string>($"""SELECT phone_e164 AS "Value" FROM customers.customers WHERE id = {id}"""));
        Assert.Equal(
            "Ana Pérez",
            await ScalarAsync<string>($"""SELECT name AS "Value" FROM customers.customer_addresses WHERE customer_id = {id}"""));
        Assert.Equal(
            "Calle 10 # 40-20",
            await ScalarAsync<string>($"""SELECT address AS "Value" FROM customers.customer_addresses WHERE customer_id = {id}"""));
        Assert.Equal(
            medellin,
            await ScalarAsync<Guid>($"""SELECT city_id AS "Value" FROM customers.customer_addresses WHERE customer_id = {id}"""));
        Assert.True(await ScalarAsync<bool>(
            $"""SELECT is_principal AS "Value" FROM customers.customer_addresses WHERE customer_id = {id}"""));
    }

    // Sin teléfono Customers no acepta la ficha completa: cae al incompleto de siempre y la aceptación no se rompe.
    [Fact]
    public async Task WithoutPhoneTheCustomerFallsBackToTheIncompleteOne()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var medellin = await WholesaleApiHarness.MedellinAsync(manager);

        var id = await EnsureCustomerAsync(tenant, "900123457", null, medellin, "Calle 10 # 40-20", "Ana Pérez");

        Assert.False(await ScalarAsync<bool>($"""SELECT (completeness = 'Complete') AS "Value" FROM customers.customers WHERE id = {id}"""));
    }

    // Una ciudad que Customers no encuentra también cae al incompleto, sin dejar entidades a medias en el contexto.
    [Fact]
    public async Task AnUnknownCityFallsBackToTheIncompleteOne()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);

        var id = await EnsureCustomerAsync(
            tenant, "900123458", "+573001234567", Guid.CreateVersion7(), "Calle 10 # 40-20", "Ana Pérez");

        Assert.False(await ScalarAsync<bool>($"""SELECT (completeness = 'Complete') AS "Value" FROM customers.customers WHERE id = {id}"""));
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                $"""SELECT count(*)::int AS "Value" FROM customers.customers WHERE tenant_id = {tenant.TenantId}"""));
    }

    // El documento ya es de un cliente: se reutiliza tal cual, sin actualizarlo con la ciudad ni el contacto nuevos.
    [Fact]
    public async Task AnExistingCustomerIsReusedWithoutBeingUpdated()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var medellin = await WholesaleApiHarness.MedellinAsync(manager);
        var first = await EnsureCustomerAsync(tenant, "900123459", null, null, null, "Ana Pérez");

        var second = await EnsureCustomerAsync(tenant, "900123459", "+573001234567", medellin, "Calle 10 # 40-20", "Otro Contacto");

        Assert.Equal(first, second);
        Assert.False(await ScalarAsync<bool>($"""SELECT (completeness = 'Complete') AS "Value" FROM customers.customers WHERE id = {first}"""));
        Assert.Equal(
            0,
            await ScalarAsync<int>(
                $"""SELECT count(*)::int AS "Value" FROM customers.customer_addresses WHERE customer_id = {first}"""));
    }

    // Spec 2026-11-23 §2-3: dos llamadas con el mismo borrador dejan una sola empresa y devuelven la misma.
    [Fact]
    public async Task TheCompanyAdapterCreatesOnceAndReuses()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var medellin = await WholesaleApiHarness.MedellinAsync(manager);
        var draft = new WholesaleCompanyDraft(
            "Distribuciones Norte SAS", "CO", "NIT", "900123456", medellin, null, "Calle 10 # 40-20", "ana@distri.co", null);

        var ids = new List<Guid>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            ids.Add(await scope.ServiceProvider.GetRequiredService<IWholesaleCompanyProvisioning>()
                .EnsureCompanyAsync(tenant.TenantId, tenant.SubjectId, draft, Ct));
        }

        Assert.Equal(ids[0], ids[1]);
        var count = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM companies.companies WHERE tenant_id = {tenant.TenantId}
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
