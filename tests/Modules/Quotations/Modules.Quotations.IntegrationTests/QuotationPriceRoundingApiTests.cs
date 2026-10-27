using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La regla de redondeo de precios del tenant (spec 2026-10-27 §3-§4), de punta a punta: la cotización y el pedido
/// la aplican cuando se recalculan, la guardan, y leerlos no la mueve. El caso de aceptación: USD con (2, Up),
/// 13,19 con 15 % da un unitario de 11,22 y, por 10 unidades, una línea de 112,20 con 19,70 de descuento.
/// </summary>
public sealed class QuotationPriceRoundingApiTests
{
    private static readonly string[] Permissions = [.. ManagerPermissions, TenancyPermissions.SettingsUpdate];

    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    [Fact]
    public async Task TheAcceptanceExampleThroughTheApi()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        await PutRuleAsync(setup, "Up");
        var quotation = await CreateUsdQuotationAsync(setup);

        var updated = await AddItemAsync(setup, quotation.Id, 10m);

        var line = Assert.Single(updated.Items);
        Assert.Equal((11.22m, 112.20m, 19.70m), (line.DiscountedUnitPrice, line.Subtotal, line.DiscountAmount));
        Assert.Equal(112.20m, updated.Total);

        // La regla usada queda escrita en las columnas de la cotización, no sólo en la respuesta.
        await using (var connection = new Npgsql.NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new Npgsql.NpgsqlCommand(
                "SELECT price_rounding_decimals::text || '/' || price_rounding_mode::text FROM quotations.quotations WHERE id = @id",
                connection);
            command.Parameters.AddWithValue("id", quotation.Id);
            Assert.Equal("2/Up", (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        var export = await setup.Client.PostAsync(
            $"{QuotationsUrl(setup.TenantId)}/{quotation.Id}/pdf", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var printed = Assert.Single(factory.PdfRenderer.LastDocument!.Items);
        Assert.Equal((11.22m, 112.20m), (printed.DiscountedUnitPrice, printed.LineTotal));
    }

    [Fact]
    public async Task GettingASentQuotationAfterARuleChangeDoesNotMoveIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var sent = await CreateSentUsdQuotationAsync(setup, factory, 10m);
        Assert.Equal((11.21m, 112.10m), (sent.Items.Single().DiscountedUnitPrice, sent.Items.Single().Subtotal));

        await PutRuleAsync(setup, "Up");
        var read = await GetQuotationAsync(setup, sent.Id);

        Assert.Equal(
            (sent.Version, sent.Total, 11.21m, 112.10m, sent.Items.Single().DiscountAmount),
            (read.Version, read.Total, read.Items.Single().DiscountedUnitPrice, read.Items.Single().Subtotal,
                read.Items.Single().DiscountAmount));
    }

    [Fact]
    public async Task EditingASentQuotationRecalculatesWithTheNewRule()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var sent = await CreateSentUsdQuotationAsync(setup, factory, 10m);
        await PutRuleAsync(setup, "Up");

        var response = await setup.Client.PutAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{sent.Id}/items/{sent.Items.Single().Id}",
            new UpdateQuotationItemRequest(20m),
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var edited = await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(edited);
        var line = Assert.Single(edited.Items);
        Assert.Equal((11.22m, 224.40m), (line.DiscountedUnitPrice, line.Subtotal));
    }

    [Fact]
    public async Task ConvertingKeepsTheQuotationRule()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var sent = await CreateSentUsdQuotationAsync(setup, factory, 10m);
        await PutRuleAsync(setup, "Up");

        var response = await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{sent.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        var (quoted, ordered) = (sent.Items.Single(), detail.Items.Single());
        Assert.Equal(
            (quoted.DiscountedUnitPrice, quoted.Subtotal, quoted.DiscountAmount, sent.Total),
            (ordered.DiscountedUnitPrice, ordered.Subtotal, ordered.DiscountAmount, detail.Order.Total));
    }

    [Fact]
    public async Task QuotationAndOrderGiveTheSameUnitAndLine()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        await PutRuleAsync(setup, "Up");
        var quotation = await AddItemAsync(setup, (await CreateUsdQuotationAsync(setup)).Id, 10m);

        var created = await setup.Client.PostAsJsonAsync(
            OrdersUrl(setup.TenantId),
            new CreateOrderRequest(
                setup.CustomerId, "Transferencia", null, false, null, null, setup.BillingAccount,
                [new OrderEditItemRequest(setup.VariantId, 10m)], "PaymentPending", []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = await created.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        var (quoted, ordered) = (quotation.Items.Single(), order.Items.Single());
        Assert.Equal(
            (11.22m, 112.20m),
            (ordered.DiscountedUnitPrice, ordered.Subtotal));
        Assert.Equal(
            (quoted.DiscountedUnitPrice, quoted.Subtotal, quoted.DiscountAmount),
            (ordered.DiscountedUnitPrice, ordered.Subtotal, ordered.DiscountAmount));
    }

    // ---- Helpers ----

    private sealed record Setup(
        HttpClient Client, Guid TenantId, Guid CustomerId, Guid VariantId, QuotationBillingAccountRequest BillingAccount);

    // Un producto con precio en USD de 13,19 y una escala plana del 15 %, y una empresa que cobra en USD.
    private static async Task<Setup> SetUpAsync(QepApiFactory factory)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(
            client,
            tenantId,
            scales: [new { fromUnit = 1, toUnit = 999_999, discount = 15m, restriction = "multiple", multiple = 1 }],
            prices: new Dictionary<string, decimal> { ["COP"] = 50_000m, ["USD"] = 13.19m });
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId, currency: "USD");
        return new Setup(
            client, tenantId, customerId, variantId,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));
    }

    private static async Task PutRuleAsync(Setup setup, string usdMode)
    {
        var response = await setup.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{setup.TenantId}/settings/price-rounding",
            new
            {
                currencies = new[]
                {
                    new { currency = "COP", decimals = 0, mode = "Nearest" },
                    new { currency = "USD", decimals = 2, mode = usdMode },
                    new { currency = "EUR", decimals = 2, mode = "Nearest" },
                },
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static Task<QuotationResponse> CreateUsdQuotationAsync(Setup setup) =>
        CreateQuotationAsync(
            setup.Client, setup.TenantId, setup.CustomerId, validUntil: null, billingAccount: setup.BillingAccount);

    private static async Task<QuotationResponse> AddItemAsync(Setup setup, Guid quotationId, decimal quantity)
    {
        var response = await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{quotationId}/items",
            new AddQuotationItemRequest(setup.VariantId, quantity),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<QuotationResponse> CreateSentUsdQuotationAsync(
        Setup setup, QepApiFactory factory, decimal quantity)
    {
        var quotation = await CreateUsdQuotationAsync(setup);
        await AddItemAsync(setup, quotation.Id, quantity);
        var pdfFileId = await CreateAvailablePdfFileAsync(setup.Client, factory, setup.TenantId);
        var response = await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var sent = await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(sent);
        return sent;
    }

    private static async Task<QuotationResponse> GetQuotationAsync(Setup setup, Guid quotationId)
    {
        var quotation = await setup.Client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(setup.TenantId)}/{quotationId}", TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        return quotation;
    }
}
