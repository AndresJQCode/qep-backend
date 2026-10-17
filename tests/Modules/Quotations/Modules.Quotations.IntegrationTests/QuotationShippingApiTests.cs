using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La entrega por envío en la cotización (spec 2026-10-17 §5.2, §5.4 y §7): crear, guardar y previsualizar con
/// <c>shipping</c>, y la respuesta con el objeto <c>shipping</c> y los totales del envío, contra Postgres real.
/// </summary>
public sealed class QuotationShippingApiTests
{
    private const string SettingsRead = "tenancy.settings.read";
    private const string SettingsUpdate = "tenancy.settings.update";

    private static readonly string[] Permissions = [.. ManagerPermissions, SettingsRead, SettingsUpdate];

    private static string QuotationUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}";

    [Fact]
    public async Task CreatesAQuotationWithChargedFreight()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);

        using var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(
                clientId, TodayInBogota().AddDays(30), null, null, null, null,
                new ShippingRequest(carrierId, "Charged", 15000m, false)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadQuotationAsync(response);
        AssertCharged(created);
        AssertCharged(await GetQuotationAsync(client, tenantId, created.Id));

        var list = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(tenantId), TestContext.Current.CancellationToken);
        var row = Assert.Single(list!.Items);
        Assert.Equal("Coordinadora", row.Shipping.Carrier?.Name);
        Assert.Equal("Charged", row.Shipping.ChargeMode);
        Assert.Equal(12605.04m, row.Shipping.FreightSubtotal);

        void AssertCharged(QuotationResponse quotation)
        {
            Assert.Equal(15000m, quotation.Total);
            Assert.Equal(0m, quotation.Subtotal);
            Assert.Equal(12605.04m, quotation.FreightSubtotal);
            Assert.Equal(2394.96m, quotation.FreightTaxAmount);
            Assert.False(quotation.Shipping.IsStorePickup);
            Assert.Equal(carrierId, quotation.Shipping.Carrier?.Id);
            Assert.Equal("Coordinadora", quotation.Shipping.Carrier?.Name);
            Assert.Equal("Charged", quotation.Shipping.ChargeMode);
            Assert.Equal(15000m, quotation.Shipping.FreightAmount);
            Assert.Equal(19, quotation.Shipping.FreightTaxPercentage);
            Assert.Equal(12605.04m, quotation.Shipping.FreightSubtotal);
            Assert.Equal(2394.96m, quotation.Shipping.FreightTaxAmount);
            Assert.False(quotation.Shipping.CashOnDelivery);
        }
    }

    [Fact]
    public async Task FreeAndOnDeliveryDoNotChangeTheTotal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        Assert.Equal(100_000m, quotation.Total);

        using var onDelivery = await PutAsync(
            client, tenantId, quotation, RequestFor(quotation, productId, new ShippingRequest(carrierId, "OnDelivery", 20000m, true)));
        var withOnDelivery = await ReadQuotationAsync(onDelivery);

        Assert.Equal(HttpStatusCode.OK, onDelivery.StatusCode);
        Assert.Equal(100_000m, withOnDelivery.Total);
        Assert.Equal(0m, withOnDelivery.FreightSubtotal);
        Assert.Equal("OnDelivery", withOnDelivery.Shipping.ChargeMode);
        Assert.Equal(20000m, withOnDelivery.Shipping.FreightAmount);
        Assert.True(withOnDelivery.Shipping.CashOnDelivery);

        using var free = await PutAsync(
            client, tenantId, withOnDelivery, RequestFor(withOnDelivery, productId, new ShippingRequest(carrierId, "Free", 0m, false)));
        var withFree = await ReadQuotationAsync(free);

        Assert.Equal(HttpStatusCode.OK, free.StatusCode);
        Assert.Equal(100_000m, withFree.Total);
        Assert.Equal("Free", withFree.Shipping.ChargeMode);
        Assert.Equal(0m, withFree.Shipping.FreightAmount);
        Assert.Equal(0m, withFree.FreightTaxAmount);
    }

    [Fact]
    public async Task SwitchingToPickupClearsTheShipping()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        using var charged = await PutAsync(
            client, tenantId, quotation, RequestFor(quotation, productId, new ShippingRequest(carrierId, "Charged", 15000m, true)));
        var withFreight = await ReadQuotationAsync(charged);
        Assert.Equal(115_000m, withFreight.Total);

        // Sin `shipping`, como un frontend viejo: pasar a recoger en tienda limpia el envío igual (P4).
        using var pickup = await PutAsync(
            client, tenantId, withFreight,
            RequestFor(withFreight, productId, shipping: null) with
            {
                Parties = new QuotationPartiesRequest(null, null, IsStorePickup: true),
            });

        Assert.Equal(HttpStatusCode.OK, pickup.StatusCode);
        var cleared = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.True(cleared.Shipping.IsStorePickup);
        Assert.Null(cleared.Shipping.Carrier);
        Assert.Null(cleared.Shipping.ChargeMode);
        Assert.Equal(0m, cleared.Shipping.FreightAmount);
        Assert.False(cleared.Shipping.CashOnDelivery);
        Assert.Equal(100_000m, cleared.Total);
    }

    // Decisión 11: nada de la entrega es obligatorio para enviar ni para convertir.
    [Fact]
    public async Task SendsAndConvertsWithoutAnyShipping()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var (quotation, _) = await CreateEditableQuotationAsync(client, factory, tenantId);
        Assert.Null(quotation.Shipping.ChargeMode);
        Assert.Null(quotation.Shipping.Carrier);

        using var converted = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.True(converted.IsSuccessStatusCode, await converted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // Un frontend que todavía no manda `shipping` no borra el envío guardado; y la versión que devuelve cada
    // guardado sirve para el siguiente, aunque el envío también toque el agregado.
    [Fact]
    public async Task OldClientsWithoutShippingKeepIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        using var charged = await PutAsync(
            client, tenantId, quotation, RequestFor(quotation, productId, new ShippingRequest(carrierId, "Charged", 15000m, false)));
        var withFreight = await ReadQuotationAsync(charged);
        Assert.Equal($"\"{withFreight.Version}\"", charged.Headers.ETag?.Tag);

        using var oldClient = await PutAsync(
            client, tenantId, withFreight, RequestFor(withFreight, productId, shipping: null) with { Notes = "Sin envío en el cuerpo" });

        Assert.Equal(HttpStatusCode.OK, oldClient.StatusCode);
        var kept = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Equal("Sin envío en el cuerpo", kept.Notes);
        Assert.Equal("Charged", kept.Shipping.ChargeMode);
        Assert.Equal(15000m, kept.Shipping.FreightAmount);
        Assert.Equal(19, kept.Shipping.FreightTaxPercentage);
        Assert.Equal("Coordinadora", kept.Shipping.Carrier?.Name);
        Assert.Equal(115_000m, kept.Total);

        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationUrl(tenantId, quotation.Id)}/history", TestContext.Current.CancellationToken);
        Assert.Contains(history!.Items, entry => entry.Details?.Contains("transportadora (Coordinadora)", StringComparison.Ordinal) == true);
    }

    // D-F15 (R15): el valor del envío no tiene de dónde re-preciarse en la moneda nueva, y el formulario
    // reenvía el `shipping` que tenía en pantalla. Si el guardado cambia la moneda, ese valor viejo no se
    // aplica: el modo y el valor quedan limpios para que el asesor los vuelva a escribir si quiere cobrar.
    [Fact]
    public async Task ChangingCurrencyClearsTheFreightEvenIfTheBodyResendsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, prices: new Dictionary<string, decimal> { ["COP"] = 100_000m, ["USD"] = 25m });
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var shipping = new ShippingRequest(carrierId, "Charged", 15000m, false);
        using var charged = await PutAsync(client, tenantId, quotation, RequestFor(quotation, productId, shipping));
        var withFreight = await ReadQuotationAsync(charged);
        Assert.Equal(115_000m, withFreight.Total);

        var usd = await CreateCompanyWithBankAccountAsync(client, tenantId, currency: "USD");
        using var switched = await PutAsync(
            client, tenantId, withFreight,
            RequestFor(withFreight, productId, shipping) with
            {
                BillingAccount = new QuotationBillingAccountRequest(usd.CompanyId, usd.BankName, usd.AccountNumber, usd.Currency),
            });
        var inUsd = await ReadQuotationAsync(switched);

        Assert.Equal("USD", inUsd.Currency);
        Assert.Null(inUsd.Shipping.ChargeMode);
        Assert.Equal(0m, inUsd.Shipping.FreightAmount);
        Assert.Equal(0m, inUsd.FreightSubtotal);
        Assert.Equal(25m, inUsd.Total);
        Assert.Equal(carrierId, inUsd.Shipping.Carrier?.Id);
        var stored = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Null(stored.Shipping.ChargeMode);
        Assert.Equal(25m, stored.Total);
    }

    [Fact]
    public async Task PreviewComputesTheFreight()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);

        using var response = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/preview",
            RequestFor(quotation, productId, new ShippingRequest(carrierId, "Charged", 15000m, false)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ReadQuotationAsync(response);
        Assert.Equal(115_000m, preview.Total);
        Assert.Equal(12605.04m, preview.FreightSubtotal);
        Assert.Equal(2394.96m, preview.FreightTaxAmount);
        Assert.Equal(quotation.Version, preview.Version);
        var stored = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Null(stored.Shipping.ChargeMode);
        Assert.Equal(100_000m, stored.Total);
    }

    [Fact]
    public async Task ChargedWithoutAFreightTaxRateIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);

        using var response = await PutAsync(
            client, tenantId, quotation, RequestFor(quotation, productId, new ShippingRequest(null, "Charged", 15000m, false)));

        await AssertCodeAsync(response, "quotation.quotation.freight_tax_rate_missing");
    }

    // Deuda de Task 3: el dominio deja pasar un valor con más de dos decimales fuera de COP; el validador lo
    // frena con el campo, que es lo que el formulario sabe marcar.
    [Fact]
    public async Task AnAmountWithMoreThanTwoDecimalsOrAnUnknownModeIsAFieldError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);

        using var response = await PutAsync(
            client, tenantId, quotation, RequestFor(quotation, productId, new ShippingRequest(null, "Paid", 10.555m, false)));

        var errors = await AssertCodeAsync(response, "validation.failed");
        Assert.True(errors.GetProperty("errors").TryGetProperty("Shipping.FreightAmount", out var amountErrors));
        Assert.True(errors.GetProperty("errors").TryGetProperty("Shipping.FreightChargeMode", out var modeErrors));
        Assert.NotEmpty(amountErrors.EnumerateArray());
        Assert.NotEmpty(modeErrors.EnumerateArray());
    }

    /// <summary>La tasa del 19 % como IVA del envío y la transportadora «Coordinadora», que hace recaudo.</summary>
    private static async Task<Guid> PrepareFreightAsync(HttpClient client, Guid tenantId)
    {
        var rateId = await CreateTaxRateAsync(client, tenantId, "IVA 19", 19);
        using var rate = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/carriers/freight-tax-rate",
            new { taxRateId = rateId },
            TestContext.Current.CancellationToken);
        rate.EnsureSuccessStatusCode();

        using var carrier = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/carriers",
            new { name = "Coordinadora", externalNames = Array.Empty<string>(), supportsCashOnDelivery = true },
            TestContext.Current.CancellationToken);
        carrier.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await carrier.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Una cotización enviada —todavía editable— con un producto de 100.000 COP sin impuesto.</summary>
    private static async Task<(QuotationResponse Quotation, Guid ProductId)> CreateEditableQuotationAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        return (quotation, productId);
    }

    private static SaveQuotationRequest RequestFor(QuotationResponse quotation, Guid productId, ShippingRequest? shipping) =>
        new(
            quotation.ValidUntil,
            quotation.PaymentMethod,
            quotation.Notes,
            null,
            quotation.BillingAccount is { } billing
                ? new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency)
                : null,
            [new QuotationEditItemRequest(productId, 1m)],
            Shipping: shipping);

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, Guid tenantId, QuotationResponse quotation, SaveQuotationRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, QuotationUrl(tenantId, quotation.Id))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.Version}\"");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<QuotationResponse> ReadQuotationAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, text);
        var quotation = JsonSerializer.Deserialize<QuotationResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(quotation);
        return quotation;
    }

    private static async Task<QuotationResponse> GetQuotationAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var quotation = await client.GetFromJsonAsync<QuotationResponse>(
            QuotationUrl(tenantId, quotationId), TestContext.Current.CancellationToken);
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
}
