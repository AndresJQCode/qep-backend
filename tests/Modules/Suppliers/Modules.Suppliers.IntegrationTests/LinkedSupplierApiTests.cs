using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;
using Modules.Suppliers.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Suppliers.IntegrationTests.SuppliersApiHarness;

namespace Modules.Suppliers.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 (mayorista), D-8: el proveedor que representa a un mayorista en QEP. El vínculo lo pone sólo la aceptación
/// de una invitación (acá se siembra por <see cref="SuppliersDbContext"/>); el formulario no lo puede poner ni quitar, y el
/// documento de un proveedor vinculado no se edita.
/// </summary>
public sealed class LinkedSupplierApiTests
{
    private const string WholesalerName = "Mayorista Origen";

    [Fact]
    public async Task AFormCannotSetOrClearTheLink()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);
        var (linkedId, wholesaler) = await SeedLinkedSupplierAsync(factory, cityId);

        // Alta con linkedTenantId en el JSON: se ignora.
        var created = await client.PostAsJsonAsync(
            SuppliersUrl(),
            ValidBody(cityId, new() { ["identificationNumber"] = "800.111.222-3", ["linkedTenantId"] = wholesaler }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await ReadAsync(created);
        Assert.Null(createdBody.LinkedTenant);
        Assert.Null((await GetAsync(client, createdBody.Id)).LinkedTenant);

        // Edición del vinculado con linkedTenantId en null: el vínculo sigue y lo demás se guarda.
        var updated = await client.PutAsJsonAsync(
            $"{SuppliersUrl()}/{linkedId}",
            ValidBody(cityId, new() { ["name"] = "Origen en QEP", ["linkedTenantId"] = null }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedBody = await ReadAsync(updated);
        Assert.Equal("Origen en QEP", updatedBody.Name);
        Assert.Equal(new SupplierLinkedTenantDto(wholesaler, WholesalerName), updatedBody.LinkedTenant);
    }

    [Fact]
    public async Task ALinkedSupplierShowsItsWholesalerInDetailAndList()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);
        var (linkedId, wholesaler) = await SeedLinkedSupplierAsync(factory, cityId);
        var external = await CreateSupplierAsync(
            client, new() { ["identificationNumber"] = "800.111.222-3" }, cityId: cityId);

        Assert.Equal(new SupplierLinkedTenantDto(wholesaler, WholesalerName), (await GetAsync(client, linkedId)).LinkedTenant);
        Assert.Null((await GetAsync(client, external.Id)).LinkedTenant);

        var list = await client.GetStringAsync(SuppliersUrl(), TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(list);
        var linkedById = document.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetGuid(), item => item.GetProperty("isLinked").GetBoolean());
        Assert.True(linkedById[linkedId]);
        Assert.False(linkedById[external.Id]);
    }

    [Fact]
    public async Task ChangingTheDocumentOfALinkedSupplierIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);
        var (linkedId, _) = await SeedLinkedSupplierAsync(factory, cityId);

        var response = await client.PutAsJsonAsync(
            $"{SuppliersUrl()}/{linkedId}",
            ValidBody(cityId, new() { ["identificationNumber"] = "900.999.999-9" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("suppliers.supplier.linked_identity_locked", await BodyAsync(response), StringComparison.Ordinal);
        Assert.Equal("900.123.456-7", (await GetAsync(client, linkedId)).IdentificationNumber);
    }

    /// <summary>
    /// Un tenant mayorista con nombre y, en el tenant de pruebas, el proveedor vinculado a él con el mismo documento que
    /// <see cref="SuppliersApiHarness.ValidBody"/>: así un PUT con ese cuerpo no cambia el documento.
    /// </summary>
    private static async Task<(Guid SupplierId, Guid Wholesaler)> SeedLinkedSupplierAsync(QepApiFactory factory, Guid cityId)
    {
        var ct = TestContext.Current.CancellationToken;
        var wholesaler = Guid.CreateVersion7();
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        tenancy.Tenants.Add(Tenant.Create(
            new Modules.Tenancy.Domain.TenantId(wholesaler), $"mayorista-{wholesaler:N}"[..30], WholesalerName, "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), DateTimeOffset.UtcNow));
        await tenancy.SaveChangesAsync(ct);

        var suppliers = scope.ServiceProvider.GetRequiredService<SuppliersDbContext>();
        var supplier = Supplier.CreateLinked(
            Guid.Parse(SuppliersApiHarness.TenantId),
            wholesaler,
            new SupplierWriteData(
                WholesalerName, "Origen Botánico SAS", "NIT", "900.123.456-7", "CO", cityId, null, null, null, null, 0,
                null, null, [], []),
            scope.ServiceProvider.GetRequiredService<ISupplierPhoneNormalizer>(),
            DateTimeOffset.UtcNow);
        suppliers.Suppliers.Add(supplier);
        await suppliers.SaveChangesAsync(ct);
        return (supplier.Id.Value, wholesaler);
    }

    private static async Task<SupplierResponse> GetAsync(HttpClient client, Guid id)
    {
        var supplier = await client.GetFromJsonAsync<SupplierResponse>(
            $"{SuppliersUrl()}/{id}", TestContext.Current.CancellationToken);
        Assert.NotNull(supplier);
        return supplier;
    }

    private static async Task<SupplierResponse> ReadAsync(HttpResponseMessage response)
    {
        var supplier = await response.Content.ReadFromJsonAsync<SupplierResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(supplier);
        return supplier;
    }
}
