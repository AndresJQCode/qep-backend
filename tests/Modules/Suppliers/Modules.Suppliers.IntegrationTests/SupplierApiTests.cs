using System.Net;
using System.Net.Http.Json;
using Modules.Suppliers.Application;
using Modules.Tenancy.Domain;
using static Modules.Suppliers.IntegrationTests.SuppliersApiHarness;

namespace Modules.Suppliers.IntegrationTests;

public sealed class SupplierApiTests
{
    [Fact]
    public async Task GetReturnsTheDetail()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var created = await CreateSupplierAsync(manager);

        using var reader = CreateReader(factory);
        var response = await reader.GetAsync(
            $"{SuppliersUrl()}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var supplier = await response.Content.ReadFromJsonAsync<SupplierResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(supplier);
        Assert.Equal(created.Id, supplier.Id);
        Assert.Equal("NIT", supplier.IdentificationType);
        Assert.NotNull(supplier.City);
        Assert.NotNull(supplier.Department);
    }

    [Fact]
    public async Task GetUnknownIs404NotFound()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync(
            $"{SuppliersUrl()}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("suppliers.supplier.not_found", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFromAnotherTenantIs403AuthorizationDenied()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync(
            $"{SuppliersUrl(OtherTenantId)}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("authorization.denied", await BodyAsync(response), StringComparison.Ordinal);
    }

    // Review Focus 4: el rol trae el permiso, pero el módulo está apagado en el tenant. El stub
    // enmascara los permisos por módulo igual que el resolvedor real.
    [Fact]
    public async Task WithTheModuleOffEveryEndpointIs403()
    {
        var (database, factory) = await OpenAsync(
            TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Suppliers));
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);

        var list = await manager.GetAsync(SuppliersUrl(), TestContext.Current.CancellationToken);
        var get = await manager.GetAsync(
            $"{SuppliersUrl()}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        var post = await manager.PostAsJsonAsync(
            SuppliersUrl(), ValidBody(Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        using var plain = CreateClient(factory, SubjectId, SuppliersApiHarness.TenantId);
        var modules = await plain.GetFromJsonAsync<ModulesDto>(
            $"/api/v1/tenants/{SuppliersApiHarness.TenantId}/modules", TestContext.Current.CancellationToken);
        Assert.NotNull(modules);
        var suppliers = Assert.Single(modules.Modules, module => module.Key == "suppliers");
        Assert.False(suppliers.Enabled);
    }

    private sealed record ModulesDto(Guid TenantId, List<ModuleDto> Modules);

    private sealed record ModuleDto(string Key, bool Enabled);
}
