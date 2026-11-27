using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Integrations.Application;
using Modules.Siigo.Application;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Tarjeta 0pQhlDt5: editar la conexión de Siigo con las credenciales de otra cuenta, de punta a punta con el host entero y Postgres
/// (<c>PUT /integrations/connections/{id}</c>). Siigo es el doble <see cref="FakeSiigoApi"/> como handler del cliente real: la comparación
/// lee las listas con las credenciales nuevas, no con la caché de la cuenta anterior.
/// </summary>
public sealed class SiigoAccountChangeApiTests
{
    private const string OtherAccountDocumentTypes = """[{"id":99999,"code":"1","name":"Factura de otra cuenta","type":"FV","active":true}]""";
    private const string OtherAccountPaymentTypes = """[{"id":7777,"name":"Otra forma de pago","type":"Cash","active":true}]""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionRoute(Guid tenantId, Guid connectionId) =>
        $"/api/v1/tenants/{tenantId}/integrations/connections/{connectionId}";

    private static string SettingsRoute(Guid tenantId, Guid connectionId, string step = "settings") =>
        $"/api/v1/tenants/{tenantId}/siigo/connections/{connectionId}/{step}";

    private static HttpClient Manager(WebApplicationFactory<Program> factory, RegisteredTenant tenant) =>
        CreateClient(
            factory,
            tenant.OwnerUserId,
            tenant.TenantId,
            [SiigoPermissions.SettingsManage, IntegrationsPermissions.ConnectionManage, IntegrationsPermissions.ConnectionRead, .. SeedPermissions]);

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string route, object body, string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, route) { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(ifMatch));
        return await client.SendAsync(request, Ct);
    }

    private sealed record World(
        WebApplicationFactory<Program> Factory, HttpClient Client, RegisteredTenant Tenant, Guid ConnectionId, FakeSiigoApi Siigo, string ConnectionString)
    {
        public Guid TenantId => Tenant.TenantId;

        public async Task<HttpResponseMessage> EditCredentialsAsync(string accessKey, bool? confirm = null)
        {
            // La versión de la conexión viaja en el cuerpo; el If-Match es esa versión entre comillas.
            var current = (await Client.GetFromJsonAsync<JsonObject>(ConnectionRoute(TenantId, ConnectionId), Ct))!;
            var etag = $"\"{current["version"]!.GetValue<long>()}\"";
            return await PutAsync(
                Client,
                ConnectionRoute(TenantId, ConnectionId),
                confirm is { } value
                    ? new { name = "Siigo principal", secrets = new Dictionary<string, string> { ["accessKey"] = accessKey }, confirmSiigoAccountChange = value }
                    : (object)new { name = "Siigo principal", secrets = new Dictionary<string, string> { ["accessKey"] = accessKey } },
                etag);
        }

        public Task<long> ConnectionVersionAsync() =>
            ScalarAsync<long>(ConnectionString, "SELECT version FROM integrations.connections WHERE id = @id", ("id", ConnectionId));

        // DBNull no se lee como texto: un comprobante borrado sale como null.
        public async Task<string?> SavedDocumentTypeAsync() =>
            await ScalarAsync<object>(ConnectionString, "SELECT document_type_id FROM siigo.settings WHERE connection_id = @id", ("id", ConnectionId)) is string id
                ? id
                : null;

        public Task<long> SettingsVersionAsync() =>
            ScalarAsync<long>(ConnectionString, "SELECT version FROM siigo.settings WHERE connection_id = @id", ("id", ConnectionId));
    }

    /// <summary>Una conexión con la configuración guardada: comprobante 24446 y la forma de pago 5101 de la cuenta de las pruebas.</summary>
    private static async Task<World> ArrangeConfiguredAsync(TestDatabase database)
    {
        var siigo = new FakeSiigoApi();
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString, siigo);
        var tenant = await RegisterTenantAsync(factory);
        await EnableSiigoAsync(connectionString, tenant.TenantId);
        var connectionId = await LinkSiigoConnectionAsync(factory, tenant.TenantId, "Siigo principal");
        var client = Manager(factory, tenant);
        (await client.GetAsync(SettingsRoute(tenant.TenantId, connectionId), Ct)).EnsureSuccessStatusCode();
        (await PutAsync(
            client,
            SettingsRoute(tenant.TenantId, connectionId, "document"),
            new { documentTypeId = "24446", costCenterId = "11", warehouseId = "3", sendToDian = true, sendMail = true, freightSeparateInvoice = true, freightProductCode = "FLETE" },
            "\"0\"")).EnsureSuccessStatusCode();
        (await PutAsync(
            client,
            SettingsRoute(tenant.TenantId, connectionId, "payments"),
            new { accounts = Array.Empty<object>(), creditPaymentTypeId = "5101", creditDays = 30, cashOnDeliveryPaymentTypeId = "5101", defaultPaymentTypeId = "5101" },
            "\"1\"")).EnsureSuccessStatusCode();
        return new World(factory, client, tenant, connectionId, siigo, connectionString);
    }

    private static void SwitchToAnotherAccount(FakeSiigoApi siigo)
    {
        siigo.On("/v1/document-types", HttpStatusCode.OK, OtherAccountDocumentTypes);
        siigo.On("/v1/payment-types", HttpStatusCode.OK, OtherAccountPaymentTypes);
    }

    [Fact]
    public async Task AnotherAccountWithoutConfirmationIs409WithTheMismatchesAndSavesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeConfiguredAsync(database);
        using var factory = world.Factory;
        using var client = world.Client;
        SwitchToAnotherAccount(world.Siigo);
        var connectionVersion = await world.ConnectionVersionAsync();
        var settingsVersion = await world.SettingsVersionAsync();

        var response = await world.EditCredentialsAsync("clave-otra-cuenta-SENTINEL");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("clave-otra-cuenta-SENTINEL", raw, StringComparison.Ordinal);
        var problem = JsonNode.Parse(raw)!.AsObject();
        Assert.Equal("siigo.connection.account_changed", problem["code"]!.GetValue<string>());
        var mismatches = problem["mismatches"]!.AsArray().Select(item => item!.AsObject()).ToList();
        Assert.Equal(
            [("documentType", "24446"), ("paymentType", "5101")],
            mismatches.Select(item => (item["kind"]!.GetValue<string>(), item["id"]!.GetValue<string>())));
        Assert.All(mismatches, item => Assert.Equal(["kind", "id"], item.Select(pair => pair.Key)));
        Assert.Equal(connectionVersion, await world.ConnectionVersionAsync());
        Assert.Equal(settingsVersion, await world.SettingsVersionAsync());
        Assert.Equal("24446", await world.SavedDocumentTypeAsync());
    }

    [Fact]
    public async Task AnotherAccountWithConfirmationSavesTheCredentialsAndResetsTheConfigurationAtomically()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeConfiguredAsync(database);
        using var factory = world.Factory;
        using var client = world.Client;
        SwitchToAnotherAccount(world.Siigo);
        var connectionVersion = await world.ConnectionVersionAsync();
        var settingsVersion = await world.SettingsVersionAsync();

        var response = await world.EditCredentialsAsync("clave-otra-cuenta-SENTINEL", confirm: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(connectionVersion + 1, await world.ConnectionVersionAsync());
        Assert.Equal(settingsVersion + 1, await world.SettingsVersionAsync());
        Assert.Null(await world.SavedDocumentTypeAsync());
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                world.ConnectionString,
                "SELECT count(*) FROM siigo.settings WHERE connection_id = @id AND (credit_payment_type_id IS NOT NULL OR default_payment_type_id IS NOT NULL OR cash_on_delivery_payment_type_id IS NOT NULL OR credit_days IS NOT NULL OR freight_product_code IS NOT NULL)",
                ("id", world.ConnectionId)));
        // Lo que no es de la cuenta se conserva.
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                world.ConnectionString,
                "SELECT count(*) FROM siigo.settings WHERE connection_id = @id AND send_to_dian AND send_mail AND freight_separate_invoice",
                ("id", world.ConnectionId)));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                world.ConnectionString,
                "SELECT count(*) FROM audit.entries WHERE action = 'siigo.settings.account_reset' AND resource_id = @id",
                ("id", world.ConnectionId.ToString())));

        var settings = (await world.Client.GetFromJsonAsync<JsonObject>(SettingsRoute(world.TenantId, world.ConnectionId), Ct))!;
        var steps = settings["readiness"]!.AsArray().Select(step => step!.AsObject()).ToDictionary(
            step => step["step"]!.GetValue<string>(), step => step["complete"]!.GetValue<bool>());
        Assert.False(steps["Document"]);
        Assert.False(steps["Taxes"]);
        Assert.False(steps["Payments"]);
        Assert.False(steps["Sellers"]);
    }

    [Fact]
    public async Task TheSameAccountWithANewKeyKeepsTheConfigurationEvenWithTheConfirmationFlag()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeConfiguredAsync(database);
        using var factory = world.Factory;
        using var client = world.Client;
        var settingsVersion = await world.SettingsVersionAsync();

        var response = await world.EditCredentialsAsync("clave-rotada-SENTINEL", confirm: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("24446", await world.SavedDocumentTypeAsync());
        Assert.Equal(settingsVersion, await world.SettingsVersionAsync());
        Assert.Equal(
            0L,
            await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'siigo.settings.account_reset'"));
    }

    [Fact]
    public async Task ASiigoOutageReadingTheListsSavesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var world = await ArrangeConfiguredAsync(database);
        using var factory = world.Factory;
        using var client = world.Client;
        // Las credenciales nuevas no tienen listas en caché (la huella es otra): Siigo está caído para ellas.
        world.Siigo.On("/v1/document-types", HttpStatusCode.ServiceUnavailable, "{}");
        var connectionVersion = await world.ConnectionVersionAsync();

        var response = await world.EditCredentialsAsync("clave-nueva-SENTINEL", confirm: true);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(connectionVersion, await world.ConnectionVersionAsync());
        Assert.Equal("24446", await world.SavedDocumentTypeAsync());
    }
}
