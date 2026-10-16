using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>La API de transportadoras y del IVA del envío (spec 2026-10-17 §4.3).</summary>
public sealed class CarrierApiTests
{
    private const string SettingsRead = "tenancy.settings.read";
    private const string SettingsUpdate = "tenancy.settings.update";
    private const string QuotationRead = "quotations.quotation.read";
    private const string TaxRateRead = "catalog.tax_rate.read";
    private const string TaxRateManage = "catalog.tax_rate.manage";

    private static string CarriersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/carriers";

    [Fact]
    public async Task CreatesListsAndOrdersCarriers()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        var first = await CreateCarrierAsync(client, tenantId, "Coordinadora", ["COORDINADORA MERCANTIL"], true);
        await CreateCarrierAsync(client, tenantId, "Servientrega", [], false);
        await CreateCarrierAsync(client, tenantId, "Envía", [], true);

        var settings = await GetSettingsAsync(client, tenantId);

        Assert.Null(settings.FreightTaxRate);
        Assert.Equal(["Coordinadora", "Servientrega", "Envía"], settings.Carriers.Select(c => c.Name));
        Assert.Equal([0, 1, 2], settings.Carriers.Select(c => c.SortOrder));
        Assert.All(settings.Carriers, carrier => Assert.True(carrier.IsActive));
        Assert.Equal(["COORDINADORA MERCANTIL"], first.ExternalNames);
        Assert.True(first.SupportsCashOnDelivery);
        Assert.Equal(1, first.Version);
    }

    [Fact]
    public async Task AnotherTenantGets403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (_, _, ownerA) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = ownerA;
        var (tenantB, _, ownerB) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var __ = ownerB;

        using var read = await ownerA.GetAsync(CarriersUrl(tenantB), TestContext.Current.CancellationToken);
        using var create = await ownerA.PostAsJsonAsync(
            CarriersUrl(tenantB),
            new { name = "Coordinadora", externalNames = Array.Empty<string>(), supportsCashOnDelivery = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task ARepeatedNameIsTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        await CreateCarrierAsync(client, tenantId, "Coordinadora", [], true);

        using var response = await PostCarrierAsync(client, tenantId, " coordinadora ", []);

        await AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "quotations.carrier.name_taken");
    }

    // Review Focus 4: un solo espacio de nombres por tenant (D-F16).
    [Fact]
    public async Task AnExternalNameEqualToAnotherCarriersNameIsTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        await CreateCarrierAsync(client, tenantId, "Servientrega", [], true);
        var coordinadora = await CreateCarrierAsync(client, tenantId, "Coordinadora", [], true);

        using var response = await PutCarrierAsync(
            client, tenantId, coordinadora.Id, coordinadora.Version, "Coordinadora", ["Servientrega"]);

        await AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "quotations.carrier.name_taken");
    }

    [Fact]
    public async Task AnInactiveCarrierStillHoldsItsName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var carrier = await CreateCarrierAsync(client, tenantId, "Coordinadora", [], true);

        using var deactivate = await client.PostAsync(
            $"{CarriersUrl(tenantId)}/{carrier.Id}/deactivate", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        var deactivated = await deactivate.Content.ReadFromJsonAsync<CarrierPayload>(TestContext.Current.CancellationToken);
        Assert.False(deactivated!.IsActive);

        using var response = await PostCarrierAsync(client, tenantId, "COORDINADORA", []);

        await AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "quotations.carrier.name_taken");

        using var activate = await client.PostAsync(
            $"{CarriersUrl(tenantId)}/{carrier.Id}/activate", content: null, TestContext.Current.CancellationToken);
        var activated = await activate.Content.ReadFromJsonAsync<CarrierPayload>(TestContext.Current.CancellationToken);
        Assert.True(activated!.IsActive);
    }

    [Fact]
    public async Task ACarrierOfAnotherTenantIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantA, _, ownerA) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = ownerA;
        var (tenantB, _, ownerB) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var __ = ownerB;
        var carrierOfB = await CreateCarrierAsync(ownerB, tenantB, "Coordinadora", [], true);

        using var response = await ownerA.PostAsync(
            $"{CarriersUrl(tenantA)}/{carrierOfB.Id}/deactivate", content: null, TestContext.Current.CancellationToken);

        await AssertCodeAsync(response, HttpStatusCode.NotFound, "quotations.carrier.not_found");
    }

    [Fact]
    public async Task UpdateRequiresIfMatchAndRejectsAStaleVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var carrier = await CreateCarrierAsync(client, tenantId, "Coordinadora", [], true);

        using var withoutIfMatch = await client.PutAsJsonAsync(
            $"{CarriersUrl(tenantId)}/{carrier.Id}",
            new { name = "Coordinadora", externalNames = Array.Empty<string>(), supportsCashOnDelivery = false },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutIfMatch.StatusCode);

        using var updated = await PutCarrierAsync(client, tenantId, carrier.Id, carrier.Version, "Coordinadora S.A.", ["Coord"]);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadFromJsonAsync<CarrierPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("Coordinadora S.A.", body!.Name);
        Assert.Equal(["Coord"], body.ExternalNames);
        Assert.Equal(2, body.Version);

        using var stale = await PutCarrierAsync(client, tenantId, carrier.Id, carrier.Version, "Otra", []);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task InvalidTextIs422OnItsField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var ownedClient = client;

        using var response = await PostCarrierAsync(client, tenantId, new string('a', 61), ["ok", ""]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", body.RootElement.GetProperty("code").GetString());
        var errors = body.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Name", out _));
        Assert.True(errors.TryGetProperty("ExternalNames[1]", out _));
    }

    [Fact]
    public async Task ReorderWithMissingOrExtraIdsIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var a = await CreateCarrierAsync(client, tenantId, "A", [], true);
        var b = await CreateCarrierAsync(client, tenantId, "B", [], true);
        var c = await CreateCarrierAsync(client, tenantId, "C", [], true);

        using var missing = await PutOrderAsync(client, tenantId, [c.Id, a.Id]);
        await AssertCodeAsync(missing, HttpStatusCode.UnprocessableEntity, "quotations.carrier.order_mismatch");
        using var extra = await PutOrderAsync(client, tenantId, [c.Id, a.Id, b.Id, Guid.NewGuid()]);
        await AssertCodeAsync(extra, HttpStatusCode.UnprocessableEntity, "quotations.carrier.order_mismatch");
        using var repeated = await PutOrderAsync(client, tenantId, [c.Id, a.Id, a.Id]);
        await AssertCodeAsync(repeated, HttpStatusCode.UnprocessableEntity, "quotations.carrier.order_mismatch");

        using var reordered = await PutOrderAsync(client, tenantId, [c.Id, a.Id, b.Id]);
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
        var body = await reordered.Content.ReadFromJsonAsync<CarrierSettingsPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(["C", "A", "B"], body!.Carriers.Select(carrier => carrier.Name));
        Assert.Equal([0, 1, 2], body.Carriers.Select(carrier => carrier.SortOrder));
    }

    [Fact]
    public async Task SetsAndClearsTheFreightTaxRate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, SettingsRead, SettingsUpdate, TaxRateRead, TaxRateManage);
        using var _ = client;
        var iva = await CreateTaxRateAsync(client, tenantId, "IVA 19", 19);

        using var set = await PutFreightTaxRateAsync(client, tenantId, iva);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var withRate = await set.Content.ReadFromJsonAsync<CarrierSettingsPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(19, withRate!.FreightTaxRate!.Percentage);
        Assert.Equal(iva, withRate.FreightTaxRate.Id);
        Assert.Equal("IVA 19", withRate.FreightTaxRate.Name);
        Assert.True(withRate.FreightTaxRate.IsActive);
        Assert.Equal(19, (await GetSettingsAsync(client, tenantId)).FreightTaxRate!.Percentage);

        using var cleared = await PutFreightTaxRateAsync(client, tenantId, null);
        var withoutRate = await cleared.Content.ReadFromJsonAsync<CarrierSettingsPayload>(TestContext.Current.CancellationToken);
        Assert.Null(withoutRate!.FreightTaxRate);
        Assert.Null((await GetSettingsAsync(client, tenantId)).FreightTaxRate);

        var old = await CreateTaxRateAsync(client, tenantId, "IVA 16", 16);
        using var deactivate = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates/{old}/deactivate", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        using var inactive = await PutFreightTaxRateAsync(client, tenantId, old);
        await AssertCodeAsync(inactive, HttpStatusCode.UnprocessableEntity, "quotations.freight_tax_rate.inactive");

        using var notFound = await PutFreightTaxRateAsync(client, tenantId, Guid.NewGuid());
        await AssertCodeAsync(notFound, HttpStatusCode.UnprocessableEntity, "quotations.freight_tax_rate.not_found");
    }

    [Fact]
    public async Task ShippingOptionsReturnOnlyActiveCarriersInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerId, admin) = await RegisterTenantAsync(
            factory, SettingsRead, SettingsUpdate, TaxRateRead, TaxRateManage);
        using var _ = admin;
        var a = await CreateCarrierAsync(admin, tenantId, "A", [], true);
        var b = await CreateCarrierAsync(admin, tenantId, "B", [], false);
        var c = await CreateCarrierAsync(admin, tenantId, "C", [], true);
        using var deactivate = await admin.PostAsync(
            $"{CarriersUrl(tenantId)}/{b.Id}/deactivate", content: null, TestContext.Current.CancellationToken);
        using var reorder = await PutOrderAsync(admin, tenantId, [c.Id, b.Id, a.Id]);
        reorder.EnsureSuccessStatusCode();
        var iva = await CreateTaxRateAsync(admin, tenantId, "IVA 19", 19);
        using var set = await PutFreightTaxRateAsync(admin, tenantId, iva);
        set.EnsureSuccessStatusCode();

        // Sólo el permiso de leer cotizaciones (D-F3): el asesor no configura, pero dibuja la entrega.
        using var advisor = CreateClient(factory, ownerId.ToString(), tenantId.ToString(), QuotationRead);
        var options = await advisor.GetFromJsonAsync<ShippingOptionsPayload>(
            $"/api/v1/tenants/{tenantId}/shipping-options", TestContext.Current.CancellationToken);

        Assert.Equal(["C", "A"], options!.Carriers.Select(carrier => carrier.Name));
        Assert.Equal([c.Id, a.Id], options.Carriers.Select(carrier => carrier.Id));
        Assert.All(options.Carriers, carrier => Assert.True(carrier.SupportsCashOnDelivery));
        Assert.Equal(19, options.FreightTaxRate!.Percentage);
    }

    // P2: el PUT del mínimo de compra no conoce la tasa del envío, así que no puede pisarla.
    [Fact]
    public async Task SavingMinimumPurchaseKeepsTheFreightTaxRate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, SettingsRead, SettingsUpdate, TaxRateRead, TaxRateManage);
        using var _ = client;
        var iva = await CreateTaxRateAsync(client, tenantId, "IVA 19", 19);
        using var set = await PutFreightTaxRateAsync(client, tenantId, iva);
        set.EnsureSuccessStatusCode();

        using var put = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/quotations/settings",
            new { minimumUnits = 10, minimumTotals = new Dictionary<string, decimal> { ["COP"] = 1m } },
            TestContext.Current.CancellationToken);
        put.EnsureSuccessStatusCode();

        Assert.Equal(iva, (await GetSettingsAsync(client, tenantId)).FreightTaxRate!.Id);
    }

    private static async Task<CarrierPayload> CreateCarrierAsync(
        HttpClient client, Guid tenantId, string name, string[] externalNames, bool supportsCashOnDelivery)
    {
        using var response = await client.PostAsJsonAsync(
            CarriersUrl(tenantId),
            new { name, externalNames, supportsCashOnDelivery },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CarrierPayload>(TestContext.Current.CancellationToken))!;
    }

    private static Task<HttpResponseMessage> PostCarrierAsync(
        HttpClient client, Guid tenantId, string name, string[] externalNames) =>
        client.PostAsJsonAsync(
            CarriersUrl(tenantId),
            new { name, externalNames, supportsCashOnDelivery = true },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PutCarrierAsync(
        HttpClient client, Guid tenantId, Guid carrierId, long version, string name, string[] externalNames)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"{CarriersUrl(tenantId)}/{carrierId}")
        {
            Content = JsonContent.Create(new { name, externalNames, supportsCashOnDelivery = true })
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PutOrderAsync(HttpClient client, Guid tenantId, Guid[] carrierIds) =>
        client.PutAsJsonAsync($"{CarriersUrl(tenantId)}/order", new { carrierIds }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PutFreightTaxRateAsync(HttpClient client, Guid tenantId, Guid? taxRateId) =>
        client.PutAsJsonAsync(
            $"{CarriersUrl(tenantId)}/freight-tax-rate", new { taxRateId }, TestContext.Current.CancellationToken);

    private static async Task<CarrierSettingsPayload> GetSettingsAsync(HttpClient client, Guid tenantId) =>
        (await client.GetFromJsonAsync<CarrierSettingsPayload>(CarriersUrl(tenantId), TestContext.Current.CancellationToken))!;

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    private sealed record CarrierPayload(
        Guid Id, string Name, string[] ExternalNames, bool SupportsCashOnDelivery, int SortOrder, bool IsActive, long Version);

    private sealed record FreightTaxRatePayload(Guid Id, string Name, int Percentage, bool IsActive);

    private sealed record CarrierSettingsPayload(FreightTaxRatePayload? FreightTaxRate, CarrierPayload[] Carriers);

    private sealed record ShippingCarrierPayload(Guid Id, string Name, bool SupportsCashOnDelivery);

    private sealed record ShippingOptionsPayload(ShippingCarrierPayload[] Carriers, FreightTaxRatePayload? FreightTaxRate);
}
