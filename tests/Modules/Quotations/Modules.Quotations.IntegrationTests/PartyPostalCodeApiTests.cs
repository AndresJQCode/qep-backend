using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-11-24 (código postal en la parte de envío): se recibe, se guarda, se devuelve y se copia al convertir. Sólo
/// la entrega lo guarda, y omitirlo lo borra. Toda lectura sale del JSON de la respuesta HTTP y no del DTO: la respuesta
/// se arma a mano y se come en silencio un campo nuevo, y el frontend decide con <c>postalCodeSupported</c> si pinta el
/// campo.
/// </summary>
public sealed class PartyPostalCodeApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    private static string QuotationUrl(Guid tenantId, Guid quotationId) => $"{QuotationsUrl(tenantId)}/{quotationId}";

    private static async Task<QuotationPartyRequest> ShippingAsync(HttpClient client, string? postalCode)
    {
        var home = (await EnsureCityIdsInTwoDepartmentsAsync(client)).First;
        return new QuotationPartyRequest(
            "Bodega Fontibón", null, "3001234567", "bodega@x.co", "Zona Franca", home.DepartmentId, home.CityId,
            PostalCode: postalCode);
    }

    // La facturación propia manda un código postal a propósito: se descarta.
    private static QuotationPartyRequest Billing() => new(
        "Lucía Fernández", "X1234567L", "+34600111222", "fact@madrid.es", "Calle Mayor 1", null, null,
        PersonType: "Natural", IdentificationType: "NIE", Country: "ES", CityName: "Madrid", PostalCode: "28013");

    private static async Task<QuotationBillingAccountRequest> AccountAsync(HttpClient client, Guid tenantId)
    {
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        return new QuotationBillingAccountRequest(
            billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static string? PostalCodeOf(JsonElement document, string role) =>
        document.GetProperty("parties").EnumerateArray()
            .Single(party => party.GetProperty("role").GetString() == role)
            .GetProperty("postalCode").GetString();

    // La cotización viene en la raíz; el pedido, en `order` del detalle.
    private static void AssertDocument(JsonElement document, string? shipping)
    {
        Assert.True(document.GetProperty("postalCodeSupported").GetBoolean());
        Assert.Equal(shipping, PostalCodeOf(document, "Shipping"));
    }

    private static async Task<HttpResponseMessage> PutQuotationAsync(
        HttpClient client, Guid tenantId, JsonElement quotation, QuotationPartiesRequest parties,
        QuotationBillingAccountRequest account)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, QuotationUrl(tenantId, quotation.GetProperty("id").GetGuid()))
        {
            Content = JsonContent.Create(new SaveQuotationRequest(
                TodayInBogota().AddDays(30), null, null, parties, account, null)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.GetProperty("version").GetInt64()}\"");
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task AQuotationSavesPreviewsReturnsAndClearsTheShippingPostalCode()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var account = await AccountAsync(client, tenantId);
        var shipping = await ShippingAsync(client, "  050021  ");
        var parties = new QuotationPartiesRequest(Billing(), shipping);
        var creation = new CreateQuotationRequest(
            customerId, TodayInBogota().AddDays(30), null, null, parties, account);

        var newPreview = await JsonAsync(await client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/preview", creation, Ct));
        var created = await JsonAsync(
            await client.PostAsJsonAsync(QuotationsUrl(tenantId), creation, Ct), HttpStatusCode.Created);
        var quotationUrl = QuotationUrl(tenantId, created.GetProperty("id").GetGuid());
        var fetched = await client.GetFromJsonAsync<JsonElement>(quotationUrl, Ct);

        foreach (var document in new[] { newPreview, created, fetched })
        {
            AssertDocument(document, "050021");
            Assert.Null(PostalCodeOf(document, "Billing"));
        }

        var editPreview = await JsonAsync(await client.PostAsJsonAsync(
            $"{quotationUrl}/preview",
            new SaveQuotationRequest(
                TodayInBogota().AddDays(30), null, null,
                parties with { Shipping = shipping with { PostalCode = "110111" } }, account, null),
            Ct));
        AssertDocument(editPreview, "110111");

        var edited = await JsonAsync(await PutQuotationAsync(
            client, tenantId, fetched, parties with { Shipping = shipping with { PostalCode = "110111" } }, account));
        AssertDocument(edited, "110111");

        // Semántica de reemplazo, como regionId: omitirlo lo borra, en el preview y en el guardado.
        var without = parties with { Shipping = shipping with { PostalCode = null } };
        var clearedPreview = await JsonAsync(await client.PostAsJsonAsync(
            $"{quotationUrl}/preview",
            new SaveQuotationRequest(TodayInBogota().AddDays(30), null, null, without, account, null),
            Ct));
        AssertDocument(clearedPreview, null);

        var cleared = await JsonAsync(await PutQuotationAsync(client, tenantId, edited, without, account));
        AssertDocument(cleared, null);
        AssertDocument(await client.GetFromJsonAsync<JsonElement>(quotationUrl, Ct), null);
    }

    [Fact]
    public async Task AManualOrderAndItsPreviewCarryTheShippingPostalCode()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = new CreateOrderRequest(
            customerId, "Transferencia", null, false, null,
            new QuotationPartiesRequest(
                Billing(), await ShippingAsync(client, "050021"), BillingVatSurplus: false),
            await AccountAsync(client, tenantId), [new OrderEditItemRequest(productId, 1m)], []);

        var preview = await JsonAsync(await client.PostAsJsonAsync($"{OrdersUrl(tenantId)}/preview", request, Ct));
        var created = await JsonAsync(
            await client.PostAsJsonAsync(OrdersUrl(tenantId), request, Ct), HttpStatusCode.Created);
        var fetched = await client.GetFromJsonAsync<JsonElement>(
            $"{OrdersUrl(tenantId)}/{created.GetProperty("order").GetProperty("id").GetGuid()}", Ct);

        foreach (var detail in new[] { preview, created, fetched })
        {
            var order = detail.GetProperty("order");
            AssertDocument(order, "050021");
            Assert.Null(PostalCodeOf(order, "Billing"));
        }
    }

    // La conversión copia la entrega y un PUT del pedido sin el bloque `delivery` la conserva. Las rutas que responden
    // sólo el pedido (/cancel…) también la traen.
    [Fact]
    public async Task ConvertingCopiesThePostalCodeAndEditingTheOrderKeepsIt()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, [.. ManagerPermissions, OrdersPermissions.OrderCancel]);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, customerId, productId,
            parties: new QuotationPartiesRequest(null, await ShippingAsync(client, "050021")));
        var quotationOrderUrl = $"{QuotationUrl(tenantId, quotation.Id)}/order";

        var converted = await JsonAsync(
            await client.PostAsJsonAsync(quotationOrderUrl, new ConvertQuotationToOrderRequest(null, []), Ct),
            HttpStatusCode.Created);
        var orderId = converted.GetProperty("order").GetProperty("id").GetGuid();
        var orderUrl = $"{OrdersUrl(tenantId)}/{orderId}";
        var byQuotation = await client.GetFromJsonAsync<JsonElement>(quotationOrderUrl, Ct);
        var edits = new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 2m)], null, null);
        var preview = await JsonAsync(await client.PostAsJsonAsync($"{orderUrl}/preview", edits, Ct));
        using var put = new HttpRequestMessage(HttpMethod.Put, orderUrl) { Content = JsonContent.Create(edits) };
        put.Headers.TryAddWithoutValidation(
            "If-Match", $"\"{converted.GetProperty("order").GetProperty("version").GetInt64()}\"");
        var saved = await JsonAsync(await client.SendAsync(put, Ct));

        foreach (var detail in new[] { converted, byQuotation, preview, saved })
        {
            AssertDocument(detail.GetProperty("order"), "050021");
        }

        var cancelled = await JsonAsync(
            await client.PostAsJsonAsync($"{orderUrl}/cancel", new CancelOrderRequest("Se canceló"), Ct));
        AssertDocument(cancelled, "050021");
    }

    [Fact]
    public async Task APostalCodeOverTwentyCharactersIs422OnItsField()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await AccountAsync(client, tenantId);
        var parties = new QuotationPartiesRequest(null, await ShippingAsync(client, new string('1', 21)));
        var created = await JsonAsync(
            await client.PostAsJsonAsync(
                QuotationsUrl(tenantId),
                new CreateQuotationRequest(customerId, TodayInBogota().AddDays(30), null, null, null, account),
                Ct),
            HttpStatusCode.Created);
        var quotation = new CreateQuotationRequest(
            customerId, TodayInBogota().AddDays(30), null, null, parties, account);
        var order = new CreateOrderRequest(
            customerId, "Transferencia", null, false, null, parties, account,
            [new OrderEditItemRequest(productId, 1m)], []);

        HttpResponseMessage[] responses =
        [
            await client.PostAsJsonAsync(QuotationsUrl(tenantId), quotation, Ct),
            await client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/preview", quotation, Ct),
            await PutQuotationAsync(client, tenantId, created, parties, account),
            await client.PostAsJsonAsync(OrdersUrl(tenantId), order, Ct),
            await client.PostAsJsonAsync($"{OrdersUrl(tenantId)}/preview", order, Ct),
        ];

        foreach (var response in responses)
        {
            var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity);
            Assert.Equal("quotations.party.postal_code_too_long", problem.GetProperty("code").GetString());
            Assert.True(problem.GetProperty("errors").TryGetProperty("Parties.Shipping.PostalCode", out var _messages));
        }
    }
}
