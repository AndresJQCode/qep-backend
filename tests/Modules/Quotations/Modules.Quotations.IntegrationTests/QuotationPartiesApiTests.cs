using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Task 1B de la tienda pública: una parte de facturación o de entrega puede estar fuera de Colombia. Allá la ciudad
/// es un texto; en Colombia sigue siendo un par de ids de DIVIPOLA y nada del contrato anterior cambia.
/// </summary>
public sealed class QuotationPartiesApiTests
{
    private static string QuotationUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}";

    private static string ConvertUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationUrl(tenantId, quotationId)}/order";

    // Natural: en un tenant colombiano una jurídica exige NIT aunque la parte sea de afuera (BillingPartyFiscalRules.cs:18-35, regla
    // por el país del tenant), y estas pruebas son del carril de ciudad, no de esa regla.
    private static QuotationPartyRequest ForeignBilling() => new(
        "Lucía Fernández", "X1234567L", "+34600111222", "fact@madrid.es", "Calle Mayor 1", null, null,
        PersonType: "Natural", IdentificationType: "NIE", Country: "ES", CityName: "Madrid");

    private static QuotationPartyRequest ForeignShipping() => new(
        "Bodega Madrid", null, "+34600111333", "bodega@madrid.es", "Calle Alcalá 20", null, null,
        Country: "ES", CityName: "Madrid");

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, Guid tenantId, QuotationResponse quotation, QuotationPartiesRequest parties)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, QuotationUrl(tenantId, quotation.Id))
        {
            Content = JsonContent.Create(new SaveQuotationRequest(
                quotation.ValidUntil,
                quotation.PaymentMethod,
                null,
                parties,
                quotation.BillingAccount is { } billing
                    ? new QuotationBillingAccountRequest(
                        billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency)
                    : null,
                null)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.Version}\"");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<QuotationResponse> ReadQuotationAsync(HttpResponseMessage response)
    {
        var quotation = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        return quotation;
    }

    private sealed record Problem(string Code, Dictionary<string, string[]>? Errors);

    [Fact]
    public async Task AQuotationSavedWithoutCountryIsColombian()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var created = await CreateQuotationAsync(client, tenantId, customerId);
        var home = (await EnsureCityIdsInTwoDepartmentsAsync(client)).First;
        var shipping = new QuotationPartyRequest(
            "Bodega Fontibón", null, "3001234567", "bodega@x.co", "Zona Franca", home.DepartmentId, home.CityId);

        var response = await PutAsync(client, tenantId, created, new QuotationPartiesRequest(null, shipping));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var party = Assert.Single((await ReadQuotationAsync(response)).Parties);
        Assert.Equal(("CO", null), (party.Country, party.CityName));
        Assert.Equal(home.CityId, party.CityId);
    }

    [Fact]
    public async Task AForeignBillingPartyRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var created = await CreateQuotationAsync(client, tenantId, customerId);

        var response = await PutAsync(
            client, tenantId, created, new QuotationPartiesRequest(ForeignBilling(), ForeignShipping()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadQuotationAsync(response);
        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            QuotationUrl(tenantId, created.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        foreach (var quotation in new[] { saved, fetched })
        {
            var billing = Assert.Single(quotation.Parties, party => party.Role == "Billing");
            Assert.Equal(("ES", "Madrid", null, null), (billing.Country, billing.CityName, billing.DepartmentId, billing.CityId));
            var shipping = Assert.Single(quotation.Parties, party => party.Role == "Shipping");
            Assert.Equal(("ES", "Madrid"), (shipping.Country, shipping.CityName));
        }
    }

    [Fact]
    public async Task ASpanishBillingPartyWithACcIsA422OnItsType()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var created = await CreateQuotationAsync(client, tenantId, customerId);

        var response = await PutAsync(
            client, tenantId, created,
            new QuotationPartiesRequest(ForeignBilling() with { IdentificationType = "CC" }, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken);
        Assert.Contains("Parties.Billing.IdentificationType", problem!.Errors!.Keys);
    }

    [Fact]
    public async Task ASpanishBillingPartyWithANifRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var created = await CreateQuotationAsync(client, tenantId, customerId);

        var response = await PutAsync(
            client, tenantId, created,
            new QuotationPartiesRequest(ForeignBilling() with { IdentificationType = "NIF" }, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            QuotationUrl(tenantId, created.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        foreach (var quotation in new[] { await ReadQuotationAsync(response), fetched })
        {
            Assert.Equal("NIF", Assert.Single(quotation.Parties, party => party.Role == "Billing").IdentificationType);
        }
    }

    [Fact]
    public async Task AForeignPartyWithDivipolaIdsIsRejectedWithItsField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var created = await CreateQuotationAsync(client, tenantId, customerId);
        var home = (await EnsureCityIdsInTwoDepartmentsAsync(client)).First;

        var response = await PutAsync(
            client, tenantId, created,
            new QuotationPartiesRequest(null, ForeignShipping() with { DepartmentId = home.DepartmentId, CityId = home.CityId }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken);
        Assert.Equal("quotations.party.city_lane_invalid", problem!.Code);
        Assert.Contains("Parties.Shipping.CityId", problem.Errors!.Keys);
    }

    [Fact]
    public async Task ConvertingAForeignQuotationCopiesItsCityText()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, customerId, productId,
            parties: new QuotationPartiesRequest(
                ForeignBilling(), ForeignShipping(), BillingWithRetention: false, BillingVatSurplus: false));

        var response = await client.PostAsJsonAsync(
            ConvertUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.All(order.Parties, party => Assert.Equal(("ES", "Madrid"), (party.Country, party.CityName)));
        Assert.Equal(2, order.Parties.Count);
    }
}
