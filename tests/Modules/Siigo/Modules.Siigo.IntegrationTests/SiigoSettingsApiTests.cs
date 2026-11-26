using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Siigo.Application;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Spec 2026-11-18 §5.3: la configuración de una conexión de Siigo de punta a punta, con el host entero (políticas, <c>If-Match</c>, el
/// mapeo central de errores y los adaptadores reales de Integrations, Companies y Catalog) contra Postgres. La API de Siigo es el doble
/// <see cref="FakeSiigoApi"/>, instalado como el handler del cliente real: el token, la caché de listas y la forma de las peticiones
/// son los de producción.
/// </summary>
public sealed class SiigoSettingsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SettingsRoute(Guid tenantId, Guid connectionId, string step = "settings") =>
        $"/api/v1/tenants/{tenantId}/siigo/connections/{connectionId}/{step}";

    private static HttpClient Manager(WebApplicationFactory<Program> factory, RegisteredTenant tenant) =>
        CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, [SiigoPermissions.SettingsManage, .. SeedPermissions]);

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string route, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, route) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue(ifMatch));
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))?["code"]?.GetValue<string>();

    private sealed record World(
        WebApplicationFactory<Program> Factory, RegisteredTenant Tenant, Guid ConnectionId, FakeSiigoApi Siigo, string ConnectionString)
    {
        public Guid TenantId => Tenant.TenantId;
    }

    private static async Task<World> ArrangeAsync(TestDatabase database, FakeSiigoApi siigo)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString, siigo);
        var tenant = await RegisterTenantAsync(factory);
        await EnableSiigoAsync(connectionString, tenant.TenantId);
        var connectionId = await LinkSiigoConnectionAsync(factory, tenant.TenantId, "Siigo principal");
        return new World(factory, tenant, connectionId, siigo, connectionString);
    }

    [Fact]
    public async Task WithoutTheModuleTheSettingsAre403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, new FakeSiigoApi());
        var tenant = await RegisterTenantAsync(factory);
        // Sin EnableSiigoAsync: siigo no está en los módulos de fábrica.
        var connectionId = await LinkSiigoConnectionAsync(factory, tenant.TenantId, "Siigo principal");
        using var client = Manager(factory, tenant);

        var response = await client.GetAsync(SettingsRoute(tenant.TenantId, connectionId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        var other = await RegisterTenantAsync(factory);
        await EnableSiigoAsync(world.ConnectionString, other.TenantId);
        var foreignConnection = await LinkSiigoConnectionAsync(factory, other.TenantId, "Siigo de otro tenant");
        using var client = Manager(factory, world.Tenant);

        var response = await client.GetAsync(SettingsRoute(world.TenantId, foreignConnection), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.settings WHERE connection_id = @id", ("id", foreignConnection)));
    }

    [Fact]
    public async Task TheFirstGetCreatesEmptySettingsWithDianOff()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);

        var response = await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"0\"", response.Headers.ETag?.Tag);
        var settings = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.False(settings["document"]!["sendToDian"]!.GetValue<bool>());
        Assert.False(settings["document"]!["sendMail"]!.GetValue<bool>());
        Assert.False(settings["document"]!["freightSeparateInvoice"]!.GetValue<bool>());
        Assert.Equal(0L, settings["version"]!.GetValue<long>());
        Assert.Equal("Siigo principal", settings["connection"]!["name"]!.GetValue<string>());
        Assert.Equal("Active", settings["connection"]!["status"]!.GetValue<string>());
        var steps = settings["readiness"]!.AsArray().Select(step => step!.AsObject()).ToList();
        Assert.Equal(["Credentials", "Company", "Document", "Taxes", "Payments", "Sellers", "Products"], steps.Select(step => step["step"]!.GetValue<string>()));
        var company = steps.Single(step => step["step"]!.GetValue<string>() == "Company");
        Assert.False(company["complete"]!.GetValue<bool>());
        Assert.Equal(["company_missing"], company["missingCodes"]!.AsArray().Select(code => code!.GetValue<string>()));
        Assert.True(steps.Single(step => step["step"]!.GetValue<string>() == "Credentials")["complete"]!.GetValue<bool>());
        Assert.Equal(1L, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.settings WHERE connection_id = @id", ("id", world.ConnectionId)));

        // Una segunda lectura no crea otra fila ni cambia la versión.
        var again = await client.GetFromJsonAsync<JsonObject>(SettingsRoute(world.TenantId, world.ConnectionId), Ct);
        Assert.Equal(0L, again!["version"]!.GetValue<long>());
        Assert.Equal(1L, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.settings"));
    }

    [Fact]
    public async Task TwoConnectionsCannotShareACompany()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        var company = await CreateCompanyAsync(client, world.TenantId);
        var second = await LinkSiigoConnectionAsync(factory, world.TenantId, "Siigo secundario");
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);
        await client.GetAsync(SettingsRoute(world.TenantId, second), Ct);

        var first = await PutAsync(client, SettingsRoute(world.TenantId, world.ConnectionId, "company"), new { companyId = company.Id }, "\"0\"");
        var conflict = await PutAsync(client, SettingsRoute(world.TenantId, second, "company"), new { companyId = company.Id }, "\"0\"");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"1\"", first.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("siigo.settings.company_taken", await CodeAsync(conflict));
        var read = await client.GetFromJsonAsync<JsonObject>(SettingsRoute(world.TenantId, second), Ct);
        Assert.Null(read!["companyId"]);
    }

    [Fact]
    public async Task ACompanyHeldByADeletedConnectionCanBeTakenAgain()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        var company = await CreateCompanyAsync(client, world.TenantId);
        var second = await LinkSiigoConnectionAsync(factory, world.TenantId, "Siigo secundario");
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);
        await client.GetAsync(SettingsRoute(world.TenantId, second), Ct);
        var first = await PutAsync(client, SettingsRoute(world.TenantId, world.ConnectionId, "company"), new { companyId = company.Id }, "\"0\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // Integrations does not clean siigo.settings (soft reference, no FK): the row outlives its connection.
        await ExecuteAsync(world.ConnectionString, "DELETE FROM integrations.connections WHERE id = @id", ("id", world.ConnectionId));

        var response = await PutAsync(client, SettingsRoute(world.TenantId, second, "company"), new { companyId = company.Id }, "\"0\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            0L,
            await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.settings WHERE connection_id = @id", ("id", world.ConnectionId)));
    }

    [Fact]
    public async Task ACompanyThatDoesNotExistIs422()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        var response = await PutAsync(client, SettingsRoute(world.TenantId, world.ConnectionId, "company"), new { companyId = Guid.CreateVersion7() }, "\"0\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("siigo.settings.company_invalid", await CodeAsync(response));
    }

    [Fact]
    public async Task AStaleIfMatchIs412AndNoneIs428()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        var company = await CreateCompanyAsync(client, world.TenantId);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        var withoutVersion = await PutAsync(client, SettingsRoute(world.TenantId, world.ConnectionId, "company"), new { companyId = company.Id }, null);
        var stale = await PutAsync(client, SettingsRoute(world.TenantId, world.ConnectionId, "company"), new { companyId = company.Id }, "\"5\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutVersion.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeAsync(stale));
        var read = await client.GetFromJsonAsync<JsonObject>(SettingsRoute(world.TenantId, world.ConnectionId), Ct);
        Assert.Equal(0L, read!["version"]!.GetValue<long>());
    }

    [Fact]
    public async Task TheDocumentStepRoundTripsAndBumpsTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        var saved = await PutAsync(
            client,
            SettingsRoute(world.TenantId, world.ConnectionId, "document"),
            new
            {
                documentTypeId = "24446",
                sellerId = "629",
                costCenterId = "11",
                warehouseId = (string?)null,
                sendToDian = true,
                sendMail = false,
                freightSeparateInvoice = true,
                freightProductCode = "FLETE",
            },
            "\"0\"");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"1\"", saved.Headers.ETag?.Tag);
        var body = (await saved.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var document = body["document"]!;
        // El cuerpo trae sellerId como lo manda un frontend viejo: se ignora, porque el vendedor es el asesor del pedido (spec 2026-11-20).
        Assert.False(document.AsObject().ContainsKey("sellerId"));
        Assert.Equal(("24446", "11", true, true, "FLETE"), (
            document["documentTypeId"]!.GetValue<string>(),
            document["costCenterId"]!.GetValue<string>(),
            document["sendToDian"]!.GetValue<bool>(),
            document["freightSeparateInvoice"]!.GetValue<bool>(),
            document["freightProductCode"]!.GetValue<string>()));
        var documentStep = body["readiness"]!.AsArray().Single(step => step!["step"]!.GetValue<string>() == "Document")!;
        Assert.True(documentStep["complete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ADocumentWithoutVoucherIs422WithTheFieldName()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        var response = await PutAsync(
            client, SettingsRoute(world.TenantId, world.ConnectionId, "document"), new { sendToDian = false }, "\"0\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal("validation.failed", problem["code"]!.GetValue<string>());
        var errors = problem["errors"]!.AsObject().Select(pair => pair.Key).ToList();
        Assert.Contains("documentTypeId", errors);
        Assert.DoesNotContain("sellerId", errors);
    }

    [Fact]
    public async Task TheTaxesStepListsTheVatRatesInUseAndTheCompanyWithholdings()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        var vat19 = await CreateTaxRateAsync(client, world.TenantId, "IVA 19", 19);
        await CreateTaxRateAsync(client, world.TenantId, "IVA 5 sin uso", 5);
        await CreateProductAsync(client, world.TenantId, "AB-1", "Producto con IVA", 10_000m, vat19);
        // Una tasa de retención necesita que el tenant ya tenga una empresa en ese país (catalog.tax_rate.country_not_available), y a una
        // empresa sólo se le asignan las municipales (ReteICA, con su ciudad).
        await CreateCompanyAsync(client, world.TenantId, "Empresa base");
        var withholding = await CreateTaxRateAsync(
            client, world.TenantId, "ReteICA 9,66", 9.66m, type: "ReteICA", country: "CO", cityId: await AnyCityIdAsync(client));
        var company = await CreateCompanyAsync(client, world.TenantId, withholdingTaxRateIds: [withholding]);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);
        var assigned = await PutAsync(client, SettingsRoute(world.TenantId, world.ConnectionId, "company"), new { companyId = company.Id }, "\"0\"");
        assigned.EnsureSuccessStatusCode();

        var settings = (await client.GetFromJsonAsync<JsonObject>(SettingsRoute(world.TenantId, world.ConnectionId), Ct))!;

        var vat = settings["taxes"]!["vat"]!.AsArray().Select(item => item!["taxPercentage"]!.GetValue<int>()).ToList();
        // El 0 siempre está; el 19 está en uso por el producto; el 5 no lo usa ninguna variante activa.
        Assert.Equal([0, 19], vat);
        var rates = settings["taxes"]!["withholdings"]!.AsArray().Select(item => item!.AsObject()).ToList();
        var rate = Assert.Single(rates);
        Assert.Equal(withholding.ToString(), rate["taxRateId"]!.GetValue<string>());
        Assert.Equal("ReteICA", rate["type"]!.GetValue<string>());
        Assert.Equal(9.66m, rate["percentage"]!.GetValue<decimal>());
        Assert.Null(rate["siigoTaxId"]);

        // El vínculo se guarda y la lista vuelve entera y en orden.
        var put = await PutAsync(
            client,
            SettingsRoute(world.TenantId, world.ConnectionId, "taxes"),
            new
            {
                vat = new object[] { new { taxPercentage = 0, siigoTaxId = (string?)null }, new { taxPercentage = 19, siigoTaxId = "13156" } },
                withholdings = new[] { new { taxRateId = withholding, siigoTaxId = "13158" } },
            },
            "\"1\"");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await put.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal(
            new (int, string?)[] { (0, null), (19, "13156") },
            saved["taxes"]!["vat"]!.AsArray().Select(item => (item!["taxPercentage"]!.GetValue<int>(), item["siigoTaxId"]?.GetValue<string>())));
        Assert.Equal("13158", saved["taxes"]!["withholdings"]![0]!["siigoTaxId"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheTaxesStepOffersTheNationalWithholdingsEvenWhenTheCompanyHasNoneAssigned()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        // Retefuente is national (the same for every company), so no company is ever assigned one: the step must offer it anyway.
        await CreateCompanyAsync(client, world.TenantId, "Empresa base");
        var retefuente = await CreateTaxRateAsync(client, world.TenantId, "Retefuente compras 2,5", 2.5m, type: "Retefuente", country: "CO");
        // A municipal rate nobody assigned to the chosen company stays out.
        await CreateTaxRateAsync(
            client, world.TenantId, "ReteICA sin asignar", 9.66m, type: "ReteICA", country: "CO", cityId: await AnyCityIdAsync(client));
        var company = await CreateCompanyAsync(client, world.TenantId);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);
        (await PutAsync(client, SettingsRoute(world.TenantId, world.ConnectionId, "company"), new { companyId = company.Id }, "\"0\"")).EnsureSuccessStatusCode();

        var settings = (await client.GetFromJsonAsync<JsonObject>(SettingsRoute(world.TenantId, world.ConnectionId), Ct))!;

        var rate = Assert.Single(settings["taxes"]!["withholdings"]!.AsArray())!.AsObject();
        Assert.Equal(retefuente.ToString(), rate["taxRateId"]!.GetValue<string>());
        Assert.Equal("Retefuente", rate["type"]!.GetValue<string>());

        // It saves like any other link.
        var put = await PutAsync(
            client,
            SettingsRoute(world.TenantId, world.ConnectionId, "taxes"),
            new
            {
                vat = new object[] { new { taxPercentage = 0, siigoTaxId = (string?)null } },
                withholdings = new[] { new { taxRateId = retefuente, siigoTaxId = "13160" } },
            },
            "\"1\"");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await put.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal("13160", saved["taxes"]!["withholdings"]![0]!["siigoTaxId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ThePaymentsStepOffersTheCompanyAccountsAndKeepsTheLinks()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        var company = await CreateCompanyAsync(client, world.TenantId);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);
        (await PutAsync(client, SettingsRoute(world.TenantId, world.ConnectionId, "company"), new { companyId = company.Id }, "\"0\"")).EnsureSuccessStatusCode();
        var key = $"BANCOLOMBIA|{company.AccountNumber}";

        var offered = (await client.GetFromJsonAsync<JsonObject>(SettingsRoute(world.TenantId, world.ConnectionId), Ct))!;
        var account = Assert.Single(offered["payments"]!["accounts"]!.AsArray())!.AsObject();
        Assert.Equal((key, "Bancolombia", true), (account["bankAccountKey"]!.GetValue<string>(), account["bankName"]!.GetValue<string>(), account["inCompany"]!.GetValue<bool>()));
        Assert.Null(account["siigoPaymentTypeId"]);

        var put = await PutAsync(
            client,
            SettingsRoute(world.TenantId, world.ConnectionId, "payments"),
            new
            {
                accounts = new[] { new { bankAccountKey = key, siigoPaymentTypeId = "5101" } },
                creditPaymentTypeId = "5102",
                creditDays = 30,
                cashOnDeliveryPaymentTypeId = "5103",
                defaultPaymentTypeId = "5101",
            },
            "\"1\"");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await put.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var payments = saved["payments"]!;
        Assert.Equal(("5101", "5102", 30, "5103", "5101"), (
            payments["accounts"]![0]!["siigoPaymentTypeId"]!.GetValue<string>(),
            payments["creditPaymentTypeId"]!.GetValue<string>(),
            payments["creditDays"]!.GetValue<int>(),
            payments["cashOnDeliveryPaymentTypeId"]!.GetValue<string>(),
            payments["defaultPaymentTypeId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task CreditDaysWithoutTheirPaymentTypeAre422()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        var response = await PutAsync(
            client, SettingsRoute(world.TenantId, world.ConnectionId, "payments"), new { accounts = Array.Empty<object>(), creditDays = 30 }, "\"0\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Contains("creditDays", problem["errors"]!.AsObject().Select(pair => pair.Key));
    }

    // Un elemento nulo de cualquier lista de la configuración es un 422 en ese elemento, no un 500.
    [Theory]
    [InlineData("taxes", """{"vat":[null],"withholdings":[]}""", "vat[0]")]
    [InlineData("taxes", """{"vat":[],"withholdings":[null]}""", "withholdings[0]")]
    [InlineData("payments", """{"accounts":[null]}""", "accounts[0]")]
    [InlineData("sellers", """{"sellers":[null]}""", "sellers[0]")]
    public async Task ANullElementOfAListIs422WithItsField(string step, string body, string field)
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        var response = await PutAsync(
            client, SettingsRoute(world.TenantId, world.ConnectionId, step), JsonNode.Parse(body)!, "\"0\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Contains(field, problem["errors"]!.AsObject().Select(pair => pair.Key));
    }

    [Fact]
    public async Task OptionsComeFromSiigoAndAreCachedForFiveMinutes()
    {
        await using var database = await StartDatabaseAsync();
        var siigo = new FakeSiigoApi();
        var world = await ArrangeAsync(database, siigo);
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);

        var first = await client.GetFromJsonAsync<JsonArray>(SettingsRoute(world.TenantId, world.ConnectionId, "options/users"), Ct);
        var second = await client.GetFromJsonAsync<JsonArray>(SettingsRoute(world.TenantId, world.ConnectionId, "options/users"), Ct);

        Assert.Equal(1, siigo.CallsTo("/v1/users"));
        Assert.Equal(1, siigo.CallsTo("/auth"));
        var option = Assert.Single(first!)!.AsObject();
        Assert.Equal(("629", "Ana Gomez"), (option["id"]!.GetValue<string>(), option["name"]!.GetValue<string>()));
        Assert.Equal(first!.ToJsonString(), second!.ToJsonString());
        var call = siigo.Calls.Single(value => value.Path == "/v1/users");
        Assert.Equal(PartnerId, call.Header("Partner-Id"));
        Assert.Equal($"Bearer {FakeSiigoApi.Token}", call.Header("Authorization"));
    }

    [Theory]
    [InlineData("document-types", "/v1/document-types")]
    [InlineData("cost-centers", "/v1/cost-centers")]
    [InlineData("warehouses", "/v1/warehouses")]
    [InlineData("payment-types", "/v1/payment-types")]
    [InlineData("taxes", "/v1/taxes")]
    public async Task EveryListOfTheWizardIsServed(string list, string siigoPath)
    {
        await using var database = await StartDatabaseAsync();
        var siigo = new FakeSiigoApi();
        var world = await ArrangeAsync(database, siigo);
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);

        var response = await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId, $"options/{list}"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, siigo.CallsTo(siigoPath));
        var items = (await response.Content.ReadFromJsonAsync<JsonArray>(Ct))!;
        Assert.NotEmpty(items);
        if (list == "taxes")
        {
            Assert.Equal(("IVA", 19m), (items[0]!["type"]!.GetValue<string>(), items[0]!["percentage"]!.GetValue<decimal>()));
        }
    }

    // Pausar es dejar de facturar, no dejar de configurar: el asistente lee de Siigo y el paso «Conexión» queda completo.
    [Fact]
    public async Task APausedConnectionCanStillBeConfigured()
    {
        await using var database = await StartDatabaseAsync();
        var siigo = new FakeSiigoApi();
        var world = await ArrangeAsync(database, siigo);
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        await ExecuteAsync(world.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");

        var options = await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId, "options/document-types"), Ct);
        var settings = await client.GetFromJsonAsync<JsonObject>(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        Assert.Equal(1, siigo.CallsTo("/v1/document-types"));
        Assert.Equal("Paused", settings!["connection"]!["status"]!.GetValue<string>());
        var credentials = settings["readiness"]!.AsArray().Single(step => step!["step"]!.GetValue<string>() == "Credentials")!;
        Assert.True(credentials["complete"]!.GetValue<bool>());
        Assert.Empty(credentials["missingCodes"]!.AsArray());
    }

    [Fact]
    public async Task AnUnknownListIs404()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);

        var response = await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId, "options/secrets"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RejectedCredentialsAreAConflictAndATransientSiigoIs503()
    {
        await using var database = await StartDatabaseAsync();
        var siigo = new FakeSiigoApi();
        var world = await ArrangeAsync(database, siigo);
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);

        siigo.On("/auth", HttpStatusCode.Unauthorized, "{}");
        var rejected = await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId, "options/users"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("siigo.connection_unavailable", await CodeAsync(rejected));

        siigo.On("/auth", HttpStatusCode.OK, $$"""{"access_token":"{{FakeSiigoApi.Token}}","expires_in":86400}""");
        siigo.On("/v1/users", HttpStatusCode.ServiceUnavailable, "{}");
        var down = await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId, "options/users"), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("siigo.unavailable", await CodeAsync(down));
    }

    [Fact]
    public async Task TheAuditNamesTheStepButNotTheSiigoValues()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = Manager(factory, world.Tenant);
        await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        var saved = await PutAsync(
            client,
            SettingsRoute(world.TenantId, world.ConnectionId, "document"),
            new { documentTypeId = "DOC-SENTINEL-1", sellerId = "SELLER-SENTINEL-2", sendToDian = false },
            "\"0\"");
        saved.EnsureSuccessStatusCode();

        var changed = await ScalarAsync<string>(
            world.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'siigo.settings.updated' AND tenant_id = @tenant ORDER BY occurred_at LIMIT 1",
            ("tenant", world.TenantId));
        Assert.Contains("document", changed, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", changed, StringComparison.Ordinal);
        Assert.Equal(
            world.ConnectionId.ToString(),
            await ScalarAsync<string>(world.ConnectionString, "SELECT resource_id FROM audit.entries WHERE action = 'siigo.settings.updated' LIMIT 1"));
    }

    [Fact]
    public async Task AUserWithoutThePermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, SiigoPermissions.InvoiceIssue);

        var response = await client.GetAsync(SettingsRoute(world.TenantId, world.ConnectionId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<(HttpClient Client, JsonObject Settings)> OpenSellersAsync(World world)
    {
        var client = Manager(world.Factory, world.Tenant);
        var settings = (await client.GetFromJsonAsync<JsonObject>(SettingsRoute(world.TenantId, world.ConnectionId), Ct))!;
        return (client, settings);
    }

    private static Guid OwnerMembership(JsonObject settings) =>
        Assert.Single(settings["sellers"]!.AsArray())!["membershipId"]!.GetValue<Guid>();

    [Fact]
    public async Task TheSettingsListEveryActiveMemberAsASeller()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;

        var (client, settings) = await OpenSellersAsync(world);
        using var _ = client;

        var owner = Assert.Single(settings["sellers"]!.AsArray())!;
        Assert.Equal(world.Tenant.Email, owner["email"]!.GetValue<string>());
        Assert.True(owner["active"]!.GetValue<bool>());
        Assert.Null(owner["siigoUserId"]);
        Assert.Null(settings["sellersAutoLinkedAt"]);
        Assert.Null(settings["productsAutoLinkedAt"]);
        var step = settings["readiness"]!.AsArray().Single(row => row!["step"]!.GetValue<string>() == "Sellers")!;
        Assert.Equal(["seller_link_missing"], step["missingCodes"]!.AsArray().Select(code => code!.GetValue<string>()));
    }

    [Fact]
    public async Task SavingSellersLinksAndCompletesTheStep()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        var (client, settings) = await OpenSellersAsync(world);
        using var _ = client;
        var owner = OwnerMembership(settings);

        var saved = await PutAsync(
            client,
            SettingsRoute(world.TenantId, world.ConnectionId, "sellers"),
            new { sellers = new[] { new { membershipId = owner, siigoUserId = "800", siigoUserName = "VANESA HERNANDEZ" } } },
            "\"0\"");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"1\"", saved.Headers.ETag?.Tag);
        var body = (await saved.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var row = Assert.Single(body["sellers"]!.AsArray())!;
        Assert.Equal(("800", "VANESA HERNANDEZ"), (row["siigoUserId"]!.GetValue<string>(), row["siigoUserName"]!.GetValue<string>()));
        var step = body["readiness"]!.AsArray().Single(value => value!["step"]!.GetValue<string>() == "Sellers")!;
        Assert.True(step["complete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ASellerOfAnotherTenantIs422()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        var (client, _) = await OpenSellersAsync(world);
        using var __ = client;

        var response = await PutAsync(
            client,
            SettingsRoute(world.TenantId, world.ConnectionId, "sellers"),
            new { sellers = new[] { new { membershipId = Guid.CreateVersion7(), siigoUserId = "800", siigoUserName = "VANESA" } } },
            "\"0\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("siigo.settings.member_not_found", await CodeAsync(response));
    }

    [Fact]
    public async Task ARepeatedMemberIs422WithTheField()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeAsync(database, new FakeSiigoApi());
        using var factory = world.Factory;
        var (client, settings) = await OpenSellersAsync(world);
        using var _ = client;
        var owner = OwnerMembership(settings);

        var response = await PutAsync(
            client,
            SettingsRoute(world.TenantId, world.ConnectionId, "sellers"),
            new
            {
                sellers = new[]
                {
                    new { membershipId = owner, siigoUserId = "800", siigoUserName = "A" },
                    new { membershipId = owner, siigoUserId = "744", siigoUserName = "B" },
                },
            },
            "\"0\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Contains("sellers", problem["errors"]!.AsObject().Select(pair => pair.Key));
    }

    [Fact]
    public async Task AutoLinkLinksMembersWhoseEmailMatches()
    {
        await using var database = await StartDatabaseAsync();
        var siigo = new FakeSiigoApi();
        var world = await ArrangeAsync(database, siigo);
        using var factory = world.Factory;
        siigo.On(
            "/v1/users",
            HttpStatusCode.OK,
            $$"""{"pagination":{"page":1,"page_size":25,"total_results":1},"results":[{"id":800,"first_name":"VANESA","last_name":"HERNANDEZ","email":" {{world.Tenant.Email.ToUpperInvariant()}} ","active":true}]}""");
        using var client = Manager(factory, world.Tenant);

        var response = await client.PostAsync(SettingsRoute(world.TenantId, world.ConnectionId, "sellers/auto-link"), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal(1, body["linked"]!.GetValue<int>());
        var row = Assert.Single(body["settings"]!["sellers"]!.AsArray())!;
        Assert.Equal(("800", "VANESA HERNANDEZ"), (row["siigoUserId"]!.GetValue<string>(), row["siigoUserName"]!.GetValue<string>()));
        Assert.NotNull(body["settings"]!["sellersAutoLinkedAt"]);
    }

    [Fact]
    public async Task AutoLinkLeavesExistingLinksAloneAndMarksTheStepEvenWithoutMatches()
    {
        await using var database = await StartDatabaseAsync();
        var siigo = new FakeSiigoApi();
        var world = await ArrangeAsync(database, siigo);
        using var factory = world.Factory;
        var (client, settings) = await OpenSellersAsync(world);
        using var _ = client;
        var owner = OwnerMembership(settings);
        (await PutAsync(
            client,
            SettingsRoute(world.TenantId, world.ConnectionId, "sellers"),
            new { sellers = new[] { new { membershipId = owner, siigoUserId = "630", siigoUserName = "OTRO" } } },
            "\"0\"")).EnsureSuccessStatusCode();
        siigo.On(
            "/v1/users",
            HttpStatusCode.OK,
            $$"""{"pagination":{"page":1,"page_size":25,"total_results":1},"results":[{"id":629,"first_name":"Ana","email":"{{world.Tenant.Email}}","active":true}]}""");

        var response = await client.PostAsync(SettingsRoute(world.TenantId, world.ConnectionId, "sellers/auto-link"), null, Ct);

        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal(0, body["linked"]!.GetValue<int>());
        Assert.Equal("630", Assert.Single(body["settings"]!["sellers"]!.AsArray())!["siigoUserId"]!.GetValue<string>());
        Assert.NotNull(body["settings"]!["sellersAutoLinkedAt"]);
    }
}
