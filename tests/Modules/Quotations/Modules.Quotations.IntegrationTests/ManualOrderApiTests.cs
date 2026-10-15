using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Spec 2026-10-15 §5.2: un pedido creado desde Pedidos, sin cotización, con su cálculo previo.</summary>
public sealed class ManualOrderApiTests
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    private static async Task<CreateOrderRequest> ValidRequestAsync(
        HttpClient client, Guid tenantId, Guid clientId, Guid productId)
    {
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        return new CreateOrderRequest(
            clientId, "Transferencia", "Pedido de mostrador", false, null, null,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
            [new OrderEditItemRequest(productId, 3m)], "PaymentPending", []);
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return problem.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task CreatesAPendingManualOrderWithItsOwnTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = await ValidRequestAsync(client, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(
            ("Pending", "Manual", (Guid?)null, "COP"),
            (detail.Order.Status, detail.Order.Source, detail.Order.QuotationId, detail.Order.Currency));
        Assert.StartsWith("PED-", detail.Order.OrderNumber, StringComparison.Ordinal);
        Assert.Equal(3m, Assert.Single(detail.Items).Quantity);
        Assert.True(detail.Order.Total > 0);
        Assert.Equal("Pedido de mostrador", detail.Order.Notes);
        Assert.Null(detail.SourceQuotation);
        Assert.Equal("Created", Assert.Single(detail.History).EventType);
        Assert.Equal(detail.Order.Id.ToString(), response.Headers.Location!.ToString().Split('/').Last());

        // Spec 2026-10-15 §4.6: el pedido manual también publica order.created, sin cotización.
        var created = Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        using var payload = JsonDocument.Parse(created.PayloadJson);
        Assert.Equal("Manual", payload.RootElement.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("quotationId").ValueKind);
    }

    [Fact]
    public async Task PreviewReturnsTotalsWithoutSaving()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = await ValidRequestAsync(client, tenantId, clientId, productId);

        var preview = await client.PostAsJsonAsync(
            $"{OrdersUrl(tenantId)}/preview", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var detail = await preview.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.True(detail.Order.Total > 0);
        Assert.Equal(3m, Assert.Single(detail.Items).Quantity);
        Assert.Equal(string.Empty, detail.Order.OrderNumber);
        var page = await client.GetFromJsonAsync<OrdersPageResponse>(OrdersUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Empty(page!.Items);

        // El preview no gasta número: el primer pedido real sigue siendo el 0001.
        var created = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.EndsWith("0001", saved!.Order.OrderNumber, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAnIncompleteCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var incompleteId = await CreateActiveCustomerAsync(client, tenantId);
        await IncompleteCustomerQuotationApiTests.MarkIncompleteAsync(database.GetConnectionString(), incompleteId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = await ValidRequestAsync(client, tenantId, incompleteId, productId);

        var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.client_incomplete", await CodeOfAsync(response));
    }

    // Review Focus 1: un cliente de otro tenant no se distingue de uno inexistente.
    [Fact]
    public async Task RejectsACustomerOfAnotherTenantAsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        using var __ = otherClient;
        var foreignCustomerId = await CreateActiveCustomerAsync(otherClient, otherTenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = await ValidRequestAsync(client, tenantId, foreignCustomerId, productId);

        var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.client_not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AValidationErrorNamesTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = (await ValidRequestAsync(client, tenantId, clientId, productId)) with { Items = [] };

        var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("Items", out var itemsErrors));
        Assert.True(itemsErrors.GetArrayLength() > 0);
    }

    [Fact]
    public async Task RequiresTheOrderManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, "quotations.order.read");
        using var _ = client;

        var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), new { }, TestContext.Current.CancellationToken);
        var preview = await client.PostAsJsonAsync(
            $"{OrdersUrl(tenantId)}/preview", new { }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
    }
}
