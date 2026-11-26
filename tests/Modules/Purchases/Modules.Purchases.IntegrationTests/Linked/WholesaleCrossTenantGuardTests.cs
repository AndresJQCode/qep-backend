using System.Net;
using System.Net.Http.Json;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.IntegrationTests.Linked;

/// <summary>
/// Mayorista fase 3, «Lectura entre tenants»: el distribuidor sólo ve y pide del catálogo de un mayorista con el que tiene una
/// relación <c>Active</c>, sólo lo visible de su lista, y nunca a través del proveedor de otro tenant.
/// </summary>
public sealed class WholesaleCrossTenantGuardTests(SharedPurchasesHost host) : IClassFixture<SharedPurchasesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PurchasesApiHarness.QepApiFactory Factory => host.Factory;

    [Fact]
    public async Task CatalogWithoutRelationshipIs403()
    {
        // Un proveedor que apunta a un tenant con el que no hay relación (la columna la fija sólo la aceptación).
        var buyer = await LinkedPurchasesHarness.NewTenantAsync(Factory, "comprador");
        var stranger = await LinkedPurchasesHarness.NewTenantAsync(Factory, "ajeno");
        using var client = LinkedPurchasesHarness.CreateBuyer(Factory, buyer);
        var supplierId = await PurchasesApiHarness.CreateSupplierAsync(client, buyer.TenantId, "Sin relación");
        await PurchasesApiHarness.ExecuteAsync(
            Factory,
            $"UPDATE suppliers.suppliers SET linked_tenant_id = {stranger.TenantId} WHERE id = {supplierId}");

        var response = await client.GetAsync(LinkedPurchasesHarness.CatalogUrl(buyer.TenantId, supplierId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(PurchasesErrorCodes.RelationshipInactive, await PurchasesApiHarness.CodeOfAsync(response));
    }

    [Fact]
    public async Task CatalogWithABlockedRelationshipIs403()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        await world.BlockRelationshipAsync();

        var response = await world.Buyer.GetAsync(world.CatalogUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(PurchasesErrorCodes.RelationshipInactive, await PurchasesApiHarness.CodeOfAsync(response));
    }

    // Spec 2026-11-20: el árbol visible y el filtro por categoría llegan por HTTP con los mismos guardas que el catálogo.
    [Fact]
    public async Task CatalogCategoriesAreServedAsAFlatList()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);

        var response = await world.Buyer.GetAsync(world.CatalogUrl() + "/categories", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await PurchasesApiHarness.BodyAsync(response));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, document.RootElement.GetProperty("items").ValueKind);
    }

    [Fact]
    public async Task CatalogCategoriesCarryOnlyWhatTheListShows()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var raiz = await world.CreateWholesalerCategoryAsync("Hogar");
        var hijo = await world.CreateWholesalerCategoryAsync("Cocina", raiz);
        var nieto = await world.CreateWholesalerCategoryAsync("Ollas", hijo);
        var otra = await world.CreateWholesalerCategoryAsync("Jardín");
        await world.UseCategoriesAsync(hijo);

        var response = await world.Buyer.GetAsync(world.CatalogUrl() + "/categories", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await PurchasesApiHarness.BodyAsync(response));
        var items = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (
                Id: item.GetProperty("id").GetGuid(),
                Name: item.GetProperty("name").GetString(),
                Parent: item.GetProperty("parentId").ValueKind == System.Text.Json.JsonValueKind.Null
                    ? (Guid?)null
                    : item.GetProperty("parentId").GetGuid()))
            .ToList();
        Assert.Equal([(hijo, "Cocina", (Guid?)null), (nieto, "Ollas", hijo)], items);
        Assert.DoesNotContain(items, item => item.Id == raiz || item.Id == otra);

        var allowed = await world.Buyer.GetAsync($"{world.CatalogUrl()}?categoryId={nieto}", Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var hidden = await world.Buyer.GetAsync($"{world.CatalogUrl()}?categoryId={otra}", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hidden.StatusCode);
        Assert.Equal(PurchasesErrorCodes.CategoryNotVisible, await PurchasesApiHarness.CodeOfAsync(hidden));
    }

    [Fact]
    public async Task CatalogCategoriesWithABlockedRelationshipIs403()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        await world.BlockRelationshipAsync();

        var response = await world.Buyer.GetAsync(world.CatalogUrl() + "/categories", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(PurchasesErrorCodes.RelationshipInactive, await PurchasesApiHarness.CodeOfAsync(response));
    }

    [Fact]
    public async Task CatalogWithAnUnknownCategoryIs422NotVisible()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);

        var response = await world.Buyer.GetAsync($"{world.CatalogUrl()}?categoryId={Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(PurchasesErrorCodes.CategoryNotVisible, await PurchasesApiHarness.CodeOfAsync(response));
    }

    [Fact]
    public async Task TheCatalogCarriesThePriceListAndTheNetCost()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);

        var page = await world.Buyer.GetFromJsonAsync<SupplierCatalogPageDto>(world.CatalogUrl(), Ct);

        Assert.Equal(("COP", LinkedPurchasesHarness.PriceListName, LinkedPurchasesHarness.DiscountPercent), (page!.Currency, page.PriceList.Name, page.PriceList.DiscountPercent));
        var item = page.Items.Single(candidate => candidate.VariantId == world.VariantA);
        Assert.Equal(
            (LinkedPurchasesHarness.BasePrice, LinkedPurchasesHarness.ListPrice, PurchaseOrderMath.NetOfVat(LinkedPurchasesHarness.ListPrice, 19m)),
            (item.BasePrice, item.ListPrice, item.UnitCost));
    }

    // Spec 2026-11-19: cada ítem trae el producto de su variante; sin foto subida, ImageUrl es null.
    [Fact]
    public async Task TheCatalogItemsCarryTheirProduct()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);

        var page = await world.Buyer.GetFromJsonAsync<SupplierCatalogPageDto>(world.CatalogUrl(), Ct);

        var item = page!.Items.Single(candidate => candidate.VariantId == world.VariantA);
        Assert.NotEqual(Guid.Empty, item.ProductId);
        Assert.False(string.IsNullOrWhiteSpace(item.ProductName));
        Assert.StartsWith(item.ProductName, item.Name, StringComparison.Ordinal);
        Assert.Null(item.ImageUrl);
    }

    [Fact]
    public async Task PreviewAndSendWithAnInvisibleProductAre422()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        await world.DeactivateWholesalerProductAsync(world.VariantA);

        var preview = await world.Buyer.PostAsJsonAsync(
            $"{PurchasesApiHarness.OrderUrl(world.BuyerTenant.TenantId)}/preview", world.DraftBody((world.VariantA, 2m)), Ct);
        var send = await PurchasesApiHarness.WriteAsync(
            world.Buyer, HttpMethod.Post, $"{PurchasesApiHarness.OrderUrl(world.BuyerTenant.TenantId, draft.Id)}/send", draft.Version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal(PurchasesErrorCodes.ProductNotVisible, await PurchasesApiHarness.CodeOfAsync(preview));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, send.StatusCode);
        Assert.Equal(PurchasesErrorCodes.ProductNotVisible, await PurchasesApiHarness.CodeOfAsync(send));
        Assert.Equal(0, await world.WholesaleOrderCountAsync());
    }

    [Fact]
    public async Task SendWithABlockedRelationshipIs403AndCreatesNoOrder()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        await world.BlockRelationshipAsync();

        var send = await PurchasesApiHarness.WriteAsync(
            world.Buyer, HttpMethod.Post, $"{PurchasesApiHarness.OrderUrl(world.BuyerTenant.TenantId, draft.Id)}/send", draft.Version);

        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
        Assert.Equal(PurchasesErrorCodes.RelationshipInactive, await PurchasesApiHarness.CodeOfAsync(send));
        Assert.Equal(0, await world.WholesaleOrderCountAsync());
        Assert.Equal("Draft", (await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, draft.Id)).Status);
    }

    // El supplierId de otro tenant no abre su catálogo: el proveedor se busca en el tenant de la ruta.
    [Fact]
    public async Task AnotherBuyerCannotReadThroughTheSupplierOfATenant()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var other = await LinkedPurchasesHarness.NewTenantAsync(Factory, "otro");
        using var otherClient = LinkedPurchasesHarness.CreateBuyer(Factory, other);

        var response = await otherClient.GetAsync(LinkedPurchasesHarness.CatalogUrl(other.TenantId, world.SupplierId), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("suppliers.supplier.not_found", await PurchasesApiHarness.CodeOfAsync(response));
    }

    // La relación se ve en Proveedores (adenda): con Blocked la pantalla no pide el catálogo.
    [Fact]
    public async Task SuppliersShowTheRelationshipStatus()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var before = await world.SupplierStatusAsync();
        await world.BlockRelationshipAsync();
        var after = await world.SupplierStatusAsync();

        Assert.Equal(("Active", "Blocked"), (before, after));
    }
}
