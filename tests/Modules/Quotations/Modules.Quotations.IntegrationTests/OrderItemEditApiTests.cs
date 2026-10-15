using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-10-15 §4.5: la cotización `Converted` queda congelada. Hasta ese spec, un pedido pendiente se
/// editaba a través de `PUT`/`DELETE /quotations/{id}/items/{itemId}` (a pedido, 2026-09-15); ahora el pedido
/// tiene sus propias líneas y se edita por `PUT /orders/{orderId}` (<see cref="OrderEditsApiTests"/>), así que
/// esas dos rutas responden `quotation.quotation.not_editable` aunque el pedido siga pendiente.
/// </summary>
public sealed class OrderItemEditApiTests
{
    private static async Task<(HttpClient Client, Guid TenantId, QuotationResponse Quotation)> ConvertedWithAPendingOrderAsync(
        QepApiFactory factory)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        return (client, tenantId, quotation);
    }

    private static async Task AssertFrozenAsync(HttpClient client, Guid tenantId, QuotationResponse before, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.quotation.not_editable", problem!.Code);
        var after = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{before.Id}", TestContext.Current.CancellationToken);
        Assert.Equal((before.Total, Assert.Single(before.Items).Quantity), (after!.Total, Assert.Single(after.Items).Quantity));
    }

    [Fact]
    public async Task UpdatingAnItemOfAConvertedQuotationIsRejectedEvenWithAPendingOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (client, tenantId, quotation) = await ConvertedWithAPendingOrderAsync(factory);
        using var _ = client;

        var response = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{Assert.Single(quotation.Items).Id}",
            new UpdateQuotationItemRequest(2m),
            TestContext.Current.CancellationToken);

        await AssertFrozenAsync(client, tenantId, quotation, response);
    }

    [Fact]
    public async Task RemovingAnItemOfAConvertedQuotationIsRejectedEvenWithAPendingOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (client, tenantId, quotation) = await ConvertedWithAPendingOrderAsync(factory);
        using var _ = client;

        var response = await client.DeleteAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{Assert.Single(quotation.Items).Id}",
            TestContext.Current.CancellationToken);

        await AssertFrozenAsync(client, tenantId, quotation, response);
    }

    private sealed record ProblemPayload(string Code);
}
