using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Crear la cotización con el documento entero (<c>POST /quotations</c> extendido) y calcularla sin id
/// (<c>POST /quotations/preview</c>), contra Postgres real (spec 2026-10-18 §7). Gemelo de
/// <see cref="ManualOrderApiTests"/> por el lado de la cotización.
/// </summary>
public sealed class QuotationOneStepCreationApiTests
{
    private const string AuditEvent = "platform.audit.recorded.v1";

    private static readonly string[] OnlyCharged = ["Charged"];

    private static string PreviewUrl(Guid tenantId) => $"{QuotationsUrl(tenantId)}/preview";

    [Fact]
    public async Task CreateWithTheWholeDocumentReturnsItsLinesAndTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var body = await BaseBodyAsync(client, tenantId);

        using var response = await PostAsync(client, QuotationsUrl(tenantId), body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadQuotationAsync(response);
        Assert.EndsWith($"/quotations/{created.Id}", response.Headers.Location?.ToString());
        Assert.Single(created.Items);
        Assert.Equal(200_000m, created.Total);
        Assert.Equal("Entregar el lunes", created.Notes);
        var stored = await GetQuotationAsync(client, tenantId, created.Id);
        Assert.Single(stored.Items);
        Assert.Equal(200_000m, stored.Total);
        Assert.Equal("Entregar el lunes", stored.Notes);
    }

    [Fact]
    public async Task CreateWithLinesLeavesCreatedAndItemAddedInTheHistory()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var body = await BaseBodyAsync(client, tenantId);

        using var response = await PostAsync(client, QuotationsUrl(tenantId), body);
        var created = await ReadQuotationAsync(response);

        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationsUrl(tenantId)}/{created.Id}/history", TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        Assert.Equal(2, history.Items.Count);
        Assert.Single(history.Items, entry => entry.EventType == "Created");
        Assert.Single(history.Items, entry => entry.Details?.Contains("Vela de soja") == true);
        var actions = (await OutboxMessagesAsync(factory, AuditEvent)).Select(ActionOf).ToArray();
        Assert.Contains("quotation.quotation.created", actions);
        Assert.Contains("quotation.quotation.item_added", actions);
    }

    [Fact]
    public async Task PreviewCalculatesWithoutPersisting()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var body = await BaseBodyAsync(client, tenantId);
        var auditBefore = (await OutboxMessagesAsync(factory, AuditEvent)).Count;

        using var response = await PostAsync(client, PreviewUrl(tenantId), body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ReadQuotationAsync(response);
        Assert.Equal(Guid.Empty, preview.Id);
        Assert.Equal(string.Empty, preview.QuotationNumber);
        Assert.Equal(0, preview.Version);
        Assert.Equal(200_000m, preview.Total);
        // La vigencia ausente vuelve resuelta: la fecha que dejaría crear.
        Assert.Equal(TodayInBogota().AddDays(15), preview.ValidUntil);
        var list = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal(0, list!.Total);
        Assert.Equal(auditBefore, (await OutboxMessagesAsync(factory, AuditEvent)).Count);
    }

    [Fact]
    public async Task PreviewDoesNotSpendTheNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await UseCounterAsync(factory, tenantId);
        var body = await BaseBodyAsync(client, tenantId);

        using var first = await PostAsync(client, PreviewUrl(tenantId), body);
        using var second = await PostAsync(client, PreviewUrl(tenantId), body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var created = await PostAsync(client, QuotationsUrl(tenantId), body);
        Assert.Equal("CT90001", (await ReadQuotationAsync(created)).QuotationNumber);
    }

    // Review Focus 1 (P1): el 422 de una línea no gasta un número del consecutivo.
    [Fact]
    public async Task ACreationRejectedByAnItemDoesNotSpendTheNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await UseCounterAsync(factory, tenantId);
        var body = await BaseBodyAsync(client, tenantId);

        using var rejected = await PostAsync(
            client, QuotationsUrl(tenantId),
            body with { Items = [new QuotationEditItemRequest(Guid.CreateVersion7(), 1m)] });
        await AssertCodeAsync(rejected, "quotation.item.product_not_found");

        using var created = await PostAsync(client, QuotationsUrl(tenantId), body);
        Assert.Equal("CT90001", (await ReadQuotationAsync(created)).QuotationNumber);
    }

    // Review Focus 4: cuenta en USD y envío cobrado, el mismo cuerpo da el mismo total en las dos rutas.
    [Fact]
    public async Task PreviewAndCreateAgreeOnTheTotal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, prices: new Dictionary<string, decimal> { ["COP"] = 100_000m, ["USD"] = 25m });
        var usd = await CreateCompanyWithBankAccountAsync(client, tenantId, "USD");
        var body = new CreateQuotationRequest(
            clientId, null, null, "Entregar el lunes", null,
            new QuotationBillingAccountRequest(usd.CompanyId, usd.BankName, usd.AccountNumber, usd.Currency),
            new ShippingRequest(null, "Charged", 10m, false),
            Items: [new QuotationEditItemRequest(productId, 2m)]);

        using var previewResponse = await PostAsync(client, PreviewUrl(tenantId), body);
        using var createResponse = await PostAsync(client, QuotationsUrl(tenantId), body);

        var preview = await ReadQuotationAsync(previewResponse);
        var created = await ReadQuotationAsync(createResponse);
        Assert.Equal("USD", preview.Currency);
        Assert.Equal("USD", created.Currency);
        Assert.Equal(60m, created.Total);
        Assert.Equal(created.Total, preview.Total);
        Assert.Equal(created.Subtotal, preview.Subtotal);
        Assert.Equal(created.TaxAmount, preview.TaxAmount);
        Assert.Equal("Charged", created.Shipping.ChargeMode);
    }

    [Fact]
    public async Task BothRoutesAreForbiddenForAnotherTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var body = await BaseBodyAsync(owner, tenantId);
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationManage);
        using var __ = otherOwner;

        using var create = await PostAsync(otherOwner, QuotationsUrl(tenantId), body);
        using var preview = await PostAsync(otherOwner, PreviewUrl(tenantId), body);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
    }

    [Fact]
    public async Task BothRoutesAreForbiddenWithoutTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var body = await BaseBodyAsync(owner, tenantId);
        using var reader = CreateClient(
            factory, owner.DefaultRequestHeaders.GetValues("X-Subject-Id").Single(), tenantId.ToString(),
            QuotationsPermissions.QuotationRead);

        using var create = await PostAsync(reader, QuotationsUrl(tenantId), body);
        using var preview = await PostAsync(reader, PreviewUrl(tenantId), body);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
    }

    // Review Focus 5: un cliente de otro tenant en la ruta propia no confirma que exista.
    [Fact]
    public async Task ACustomerOfAnotherTenantIsNotFoundInBothRoutes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var body = await BaseBodyAsync(client, tenantId);
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = otherClient;
        var foreignCustomerId = await CreateActiveCustomerAsync(otherClient, otherTenantId);

        using var create = await PostAsync(client, QuotationsUrl(tenantId), body with { ClientId = foreignCustomerId });
        using var preview = await PostAsync(client, PreviewUrl(tenantId), body with { ClientId = foreignCustomerId });

        await AssertCodeAsync(create, "quotation.quotation.client_not_found");
        await AssertCodeAsync(preview, "quotation.quotation.client_not_found");
    }

    [Fact]
    public async Task AProductOfAnotherTenantIsTheSaveError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var body = await BaseBodyAsync(client, tenantId);
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = otherClient;
        var foreignProductId = await CreateProductWithScalesAsync(otherClient, otherTenantId, baseCop: 100_000m);
        var foreign = body with { Items = [new QuotationEditItemRequest(foreignProductId, 1m)] };

        using var create = await PostAsync(client, QuotationsUrl(tenantId), foreign);
        using var preview = await PostAsync(client, PreviewUrl(tenantId), foreign);

        await AssertCodeAsync(create, "quotation.item.product_not_found");
        await AssertCodeAsync(preview, "quotation.item.product_not_found");
    }

    [Fact]
    public async Task AnIncompleteCustomerIsRejectedInBothRoutes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var body = await BaseBodyAsync(client, tenantId);
        await IncompleteCustomerQuotationApiTests.MarkIncompleteAsync(database.GetConnectionString(), body.ClientId);

        using var create = await PostAsync(client, QuotationsUrl(tenantId), body);
        using var preview = await PostAsync(client, PreviewUrl(tenantId), body);

        await AssertCodeAsync(create, "quotation.quotation.client_incomplete");
        await AssertCodeAsync(preview, "quotation.quotation.client_incomplete");
    }

    [Fact]
    public async Task ARepeatedProductIsAFieldErrorInBothRoutes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var body = await BaseBodyAsync(client, tenantId);
        var productId = body.Items![0].ProductId;
        var repeated = body with
        {
            Items = [new QuotationEditItemRequest(productId, 1m), new QuotationEditItemRequest(productId, 2m)],
        };

        using var create = await PostAsync(client, QuotationsUrl(tenantId), repeated);
        using var preview = await PostAsync(client, PreviewUrl(tenantId), repeated);

        foreach (var response in new[] { create, preview })
        {
            var problem = await AssertCodeAsync(response, "validation.failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("Items", out var itemsErrors), problem.ToString());
            Assert.NotEqual(0, itemsErrors.GetArrayLength());
        }
    }

    // Spec 2026-10-17 §4.4: una cotización nueva no tiene modo guardado, así que un modo apagado se rechaza.
    [Fact]
    public async Task ADisabledFreightChargeModeIsRejectedInBothRoutes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, "tenancy.settings.read", "tenancy.settings.update"]);
        using var _ = client;
        using (var modes = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/carriers/freight-charge-modes",
            new { enabledModes = OnlyCharged },
            TestContext.Current.CancellationToken))
        {
            modes.EnsureSuccessStatusCode();
        }

        var body = await BaseBodyAsync(client, tenantId) with { Shipping = new ShippingRequest(null, "Free", 0m, false) };

        using var create = await PostAsync(client, QuotationsUrl(tenantId), body);
        using var preview = await PostAsync(client, PreviewUrl(tenantId), body);

        await AssertCodeAsync(create, "quotation.quotation.freight_charge_mode_disabled");
        await AssertCodeAsync(preview, "quotation.quotation.freight_charge_mode_disabled");
    }

    /// <summary>Un cliente activo y una línea de 2 × 100.000 COP sin impuesto, con notas y sin vigencia.</summary>
    private static async Task<CreateQuotationRequest> BaseBodyAsync(HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        return new CreateQuotationRequest(
            clientId, null, null, "Entregar el lunes", null, null,
            Items: [new QuotationEditItemRequest(productId, 2m)]);
    }

    // El formato «CT» sin año y el contador en 90.001, como QuotationApiTests.CreateUsesThePrefixAndCounterConfiguredForTheTenant.
    private static async Task UseCounterAsync(QepApiFactory factory, Guid tenantId)
    {
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, tenantId, "quotation", "CT", includeYear: false, "", 5);
        await QuotationApiTests.SetQuotationCounterAsync(factory, tenantId, year: 0, nextValue: 90_001L);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, CreateQuotationRequest body) =>
        client.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);

    private static async Task<QuotationResponse> ReadQuotationAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        var quotation = JsonSerializer.Deserialize<QuotationResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(quotation);
        return quotation;
    }

    private static async Task<QuotationResponse> GetQuotationAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var quotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}", TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        return quotation;
    }

    private static async Task<JsonElement> AssertCodeAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"Expected 422, got {response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    // Misma lectura que QuotationEditsApiTests.ActionOf: el payload del evento de auditoría lleva la acción en `action`.
    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }
}
