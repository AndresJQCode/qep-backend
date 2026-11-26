using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Regalos en el pedido (specs 2026-11-21 y 2026-11-23): viajan en la lista <c>gifts</c> y no suben el total.</summary>
public sealed class OrderGiftApiTests(SharedQuotationsHost sharedHost) : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    // Las rutas del spec 2026-11-21 se quitaron: el regalo viaja sólo en la lista `gifts` del POST y el PUT.
    [Fact]
    public async Task TheOldGiftRoutesNoLongerExist()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 1_000m, scales: NoDiscountScales());
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders",
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new OrderEditItemRequest(productId, 10m)], []),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = await created.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        var giftsUrl = $"{OrderByIdUrl(tenantId, order.Order.Id)}/gifts";

        var posted = await client.PostAsJsonAsync(
            giftsUrl, new { variantId = productId, quantity = 1m }, TestContext.Current.CancellationToken);
        var deleted = await client.DeleteAsync($"{giftsUrl}/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        var fetched = await client.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(tenantId, order.Order.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.DoesNotContain(fetched.Items, item => item.IsGift);
    }

    // Spec 2026-11-23: los regalos viajan en su propia lista al crear, al calcular y al editar.
    private sealed record Setup(
        HttpClient Client, Guid TenantId, Guid ClientId, Guid ProductA, Guid ProductB, QuotationBillingAccountRequest Account);

    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    // A cuesta 1.000 y B 2.000, con IVA incluido y sin escalas: 10 unidades de A suman 10.000.
    private async Task<Setup> NewSetupAsync()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productA = await CreateProductWithScalesAsync(client, tenantId, baseCop: 1_000m, scales: NoDiscountScales());
        var productB = await CreateProductWithScalesAsync(client, tenantId, baseCop: 2_000m, scales: NoDiscountScales());
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var account = new QuotationBillingAccountRequest(
            billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency);
        return new Setup(client, tenantId, clientId, productA, productB, account);
    }

    private static CreateOrderRequest NewOrderBody(Setup setup, params OrderGiftLineRequest[] gifts) =>
        new CreateOrderRequest(
            setup.ClientId, "Transferencia", null, false, null, null, setup.Account,
            [new OrderEditItemRequest(setup.ProductA, 10m)], [])
        {
            Gifts = gifts.Length == 0 ? null : gifts,
        };

    private static async Task<OrderDetailResponse> ReadDetailAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {text}");
        var detail = System.Text.Json.JsonSerializer.Deserialize<OrderDetailResponse>(
            text, System.Text.Json.JsonSerializerOptions.Web);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<HttpResponseMessage> PutAsync(Setup setup, OrderResponse order, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, OrderByIdUrl(setup.TenantId, order.Id))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{order.Version}\"");
        return await setup.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateOrderWithAGiftOfAPaidVariant()
    {
        var setup = await NewSetupAsync();
        using var _ = setup.Client;
        var withoutGift = await ReadDetailAsync(
            await setup.Client.PostAsJsonAsync(
                OrdersUrl(setup.TenantId), NewOrderBody(setup), TestContext.Current.CancellationToken),
            HttpStatusCode.Created);

        var response = await setup.Client.PostAsJsonAsync(
            OrdersUrl(setup.TenantId),
            NewOrderBody(setup, new OrderGiftLineRequest(setup.ProductA, 1m)),
            TestContext.Current.CancellationToken);

        var detail = await ReadDetailAsync(response, HttpStatusCode.Created);
        Assert.Equal(2, detail.Items.Count);
        var gift = Assert.Single(detail.Items, item => item.IsGift);
        Assert.Equal(setup.ProductA, gift.VariantId);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(0m, gift.Subtotal);
        var paid = Assert.Single(detail.Items, item => !item.IsGift);
        Assert.Equal(setup.ProductA, paid.VariantId);
        Assert.Equal(10m, paid.Quantity);
        Assert.Equal(withoutGift.Order.Total, detail.Order.Total);
        // El alta de un pedido con regalos deja sólo «Creado a mano», como con las líneas pagadas.
        Assert.Equal("Created", Assert.Single(detail.History).EventType);
    }

    [Fact]
    public async Task PreviewNewOrderPaintsTheGift()
    {
        var setup = await NewSetupAsync();
        using var _ = setup.Client;

        var response = await setup.Client.PostAsJsonAsync(
            $"{OrdersUrl(setup.TenantId)}/preview",
            NewOrderBody(setup, new OrderGiftLineRequest(setup.ProductA, 1m)),
            TestContext.Current.CancellationToken);

        var detail = await ReadDetailAsync(response, HttpStatusCode.OK);
        Assert.Equal(10_000m, detail.Order.Total);
        var gift = Assert.Single(detail.Items, item => item.IsGift);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(string.Empty, detail.Order.OrderNumber);
        var page = await setup.Client.GetFromJsonAsync<OrdersPageResponse>(
            OrdersUrl(setup.TenantId), TestContext.Current.CancellationToken);
        Assert.Empty(page!.Items);
    }

    [Fact]
    public async Task PutWithGiftsAddsChangesAndRemoves()
    {
        var setup = await NewSetupAsync();
        using var _ = setup.Client;
        var created = await ReadDetailAsync(
            await setup.Client.PostAsJsonAsync(
                OrdersUrl(setup.TenantId),
                NewOrderBody(setup, new OrderGiftLineRequest(setup.ProductA, 1m)),
                TestContext.Current.CancellationToken),
            HttpStatusCode.Created);
        var paidItems = new[] { new OrderEditItemRequest(setup.ProductA, 10m) };

        // A cambia de cantidad y B entra.
        var edited = await ReadDetailAsync(
            await PutAsync(setup, created.Order, new SaveOrderEditsRequest(paidItems, null, null)
            {
                Gifts = [new OrderGiftLineRequest(setup.ProductA, 3m), new OrderGiftLineRequest(setup.ProductB, 2m)],
            }),
            HttpStatusCode.OK);

        Assert.Equal(3m, Assert.Single(edited.Items, item => item.IsGift && item.VariantId == setup.ProductA).Quantity);
        Assert.Equal(2m, Assert.Single(edited.Items, item => item.IsGift && item.VariantId == setup.ProductB).Quantity);
        Assert.Equal(10m, Assert.Single(edited.Items, item => !item.IsGift).Quantity);
        Assert.Equal(created.Order.Total, edited.Order.Total);
        Assert.Contains(edited.History, entry =>
            entry.EventType == "ItemsChanged" && entry.Details!.StartsWith("Cambió la cantidad del regalo", StringComparison.Ordinal));
        Assert.Contains(edited.History, entry =>
            entry.EventType == "ItemsChanged" && entry.Details!.StartsWith("Agregó de regalo", StringComparison.Ordinal));

        // La lista entera: lo que no viene se quita.
        var trimmed = await ReadDetailAsync(
            await PutAsync(setup, edited.Order, new SaveOrderEditsRequest(paidItems, null, null)
            {
                Gifts = [new OrderGiftLineRequest(setup.ProductB, 2m)],
            }),
            HttpStatusCode.OK);

        var remaining = Assert.Single(trimmed.Items, item => item.IsGift);
        Assert.Equal(setup.ProductB, remaining.VariantId);
        Assert.Contains(trimmed.History, entry =>
            entry.EventType == "ItemsChanged" && entry.Details!.StartsWith("Quitó el regalo", StringComparison.Ordinal));

        // Una lista vacía los quita todos.
        var cleared = await ReadDetailAsync(
            await PutAsync(setup, trimmed.Order, new SaveOrderEditsRequest(paidItems, null, null) { Gifts = [] }),
            HttpStatusCode.OK);
        Assert.DoesNotContain(cleared.Items, item => item.IsGift);
    }

    [Fact]
    public async Task PutWithoutGiftsKeepsTheSavedGift()
    {
        var setup = await NewSetupAsync();
        using var _ = setup.Client;
        var created = await ReadDetailAsync(
            await setup.Client.PostAsJsonAsync(
                OrdersUrl(setup.TenantId),
                NewOrderBody(setup, new OrderGiftLineRequest(setup.ProductA, 2m)),
                TestContext.Current.CancellationToken),
            HttpStatusCode.Created);

        // Un cuerpo sin la propiedad `gifts`, como el del frontend anterior.
        var edited = await ReadDetailAsync(
            await PutAsync(setup, created.Order, new
            {
                items = new[] { new { variantId = setup.ProductA, quantity = 12m } },
                notes = "Cambio de cantidad",
            }),
            HttpStatusCode.OK);

        Assert.Equal(12m, Assert.Single(edited.Items, item => !item.IsGift).Quantity);
        var gift = Assert.Single(edited.Items, item => item.IsGift);
        Assert.Equal(2m, gift.Quantity);
        Assert.DoesNotContain(edited.History, entry => entry.Details!.Contains("regalo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewEditsPaintsAGift()
    {
        var setup = await NewSetupAsync();
        using var _ = setup.Client;
        var created = await ReadDetailAsync(
            await setup.Client.PostAsJsonAsync(
                OrdersUrl(setup.TenantId), NewOrderBody(setup), TestContext.Current.CancellationToken),
            HttpStatusCode.Created);

        var preview = await ReadDetailAsync(
            await setup.Client.PostAsJsonAsync(
                $"{OrderByIdUrl(setup.TenantId, created.Order.Id)}/preview",
                new PreviewOrderEditsRequest([new OrderEditItemRequest(setup.ProductA, 10m)], null, null)
                {
                    Gifts = [new OrderGiftLineRequest(setup.ProductB, 4m)],
                },
                TestContext.Current.CancellationToken),
            HttpStatusCode.OK);

        var gift = Assert.Single(preview.Items, item => item.IsGift);
        Assert.Equal(setup.ProductB, gift.VariantId);
        Assert.Equal(4m, gift.Quantity);
        Assert.Equal(created.Order.Version, preview.Order.Version);
        Assert.Equal(created.Order.Total, preview.Order.Total);

        // El cálculo previo no guarda nada.
        var stored = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(setup.TenantId, created.Order.Id), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(stored!.Items, item => item.IsGift);
        Assert.Equal(created.Order.Version, stored.Order.Version);
    }

    [Fact]
    public async Task CreateOrderRejectsARepeatedGift()
    {
        var setup = await NewSetupAsync();
        using var _ = setup.Client;

        var response = await setup.Client.PostAsJsonAsync(
            OrdersUrl(setup.TenantId),
            NewOrderBody(
                setup, new OrderGiftLineRequest(setup.ProductA, 1m), new OrderGiftLineRequest(setup.ProductA, 2m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("Gifts", out var errors));
        Assert.True(errors.GetArrayLength() > 0);
        var page = await setup.Client.GetFromJsonAsync<OrdersPageResponse>(
            OrdersUrl(setup.TenantId), TestContext.Current.CancellationToken);
        Assert.Empty(page!.Items);
    }
}
