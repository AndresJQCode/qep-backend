using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// La libreta de perfiles de facturación (spec 2026-11-24 §3): cuatro sub-rutas que responden el cliente entero. Las
/// respuestas se leen como JSON, porque el contrato que se prueba es el de la red.
/// </summary>
public sealed class CustomerBillingProfilesApiTests
{
    private static string ProfilesUrl(Guid customerId, string tenantId = TenantId) =>
        $"{CustomersUrl(tenantId)}/{customerId}/billing-profiles";

    // Diccionarios y no records: cada prueba necesita omitir o mandar campos sueltos.
    private static Dictionary<string, object?> ColombianBody(
        Guid cityId, string name = "Comercial Andina SAS", string identificationNumber = "900123456") => new()
        {
            ["name"] = name,
            ["personType"] = "Juridica",
            ["identificationType"] = "NIT",
            ["identificationNumber"] = identificationNumber,
            ["phone"] = "3001234567",
            ["email"] = "facturas@andina.co",
            ["address"] = "Cra 43A # 1-50",
            ["country"] = "CO",
            ["cityId"] = cityId,
            ["vatSurplus"] = true,
            ["isPrincipal"] = false
        };

    private static Dictionary<string, object?> SpanishBody(Guid? regionId, string name = "Andina Iberia SL") => new()
    {
        ["name"] = name,
        ["identificationType"] = "NIF",
        ["identificationNumber"] = "B-12345678",
        ["address"] = "Calle Mayor 1",
        ["country"] = "ES",
        ["cityName"] = "Madrid",
        ["regionId"] = regionId,
        ["isPrincipal"] = false
    };

    private static async Task<JsonElement> ReadAsync(
        HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, Guid customerId, Dictionary<string, object?> body, string tenantId = TenantId) =>
        client.PostAsJsonAsync(ProfilesUrl(customerId, tenantId), body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, Guid customerId, Guid profileId, Dictionary<string, object?> body) =>
        client.PutAsJsonAsync(
            $"{ProfilesUrl(customerId)}/{profileId}", body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid customerId, Guid profileId) =>
        client.DeleteAsync($"{ProfilesUrl(customerId)}/{profileId}", TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> MakePrincipalAsync(HttpClient client, Guid customerId, Guid profileId) =>
        client.PostAsync(
            $"{ProfilesUrl(customerId)}/{profileId}/principal", content: null, TestContext.Current.CancellationToken);

    private static JsonElement[] Profiles(JsonElement customer) =>
        [.. customer.GetProperty("billingProfiles").EnumerateArray()];

    private static async Task<CustomerResponse> NewCustomerAsync(HttpClient client, CityFixture city)
    {
        var classification = await CreateClassificationAsync(client);
        return await CreateCustomerAsync(client, city.CityId, classification.Id);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task PostingAColombianProfileReturnsTheCustomerWithResolvedCityAndLimit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var rate = await InsertTaxRateAsync(
            database.GetConnectionString(), "Retefuente servicios", "Retefuente", 2.5m);
        var body = ColombianBody(city.CityId);
        body["withholdingTaxRateIds"] = new[] { rate, rate };

        var response = await ReadAsync(await PostAsync(client, customer.Id, body));

        Assert.Equal(customer.Id, response.GetProperty("id").GetGuid());
        Assert.Equal(50, response.GetProperty("billingProfileLimit").GetInt32());
        var profile = Assert.Single(Profiles(response));
        Assert.NotEqual(Guid.Empty, profile.GetProperty("id").GetGuid());
        Assert.Equal("Comercial Andina SAS", profile.GetProperty("name").GetString());
        Assert.Equal("Juridica", profile.GetProperty("personType").GetString());
        Assert.Equal("NIT", profile.GetProperty("identificationType").GetString());
        Assert.Equal("900123456", profile.GetProperty("identificationNumber").GetString());
        Assert.Equal("3001234567", profile.GetProperty("phone").GetString());
        Assert.Equal("facturas@andina.co", profile.GetProperty("email").GetString());
        Assert.Equal("Cra 43A # 1-50", profile.GetProperty("address").GetString());
        Assert.Equal("CO", profile.GetProperty("country").GetString());
        Assert.Equal(city.CityId, profile.GetProperty("cityId").GetGuid());
        Assert.Equal(city.CityName, profile.GetProperty("cityName").GetString());
        Assert.Equal(city.DepartmentId, profile.GetProperty("departmentId").GetGuid());
        Assert.Equal(city.DepartmentName, profile.GetProperty("departmentName").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("region").ValueKind);
        var tax = Assert.Single(profile.GetProperty("withholdingTaxRates").EnumerateArray());
        Assert.Equal(rate, tax.GetProperty("id").GetGuid());
        Assert.Equal("Retefuente servicios", tax.GetProperty("name").GetString());
        Assert.Equal("Retefuente", tax.GetProperty("type").GetString());
        Assert.Equal("CO", tax.GetProperty("country").GetString());
        Assert.Equal(2.5m, tax.GetProperty("percentage").GetDecimal());
        Assert.True(tax.GetProperty("isActive").GetBoolean());
        Assert.Equal("All", tax.GetProperty("appliesTo").GetString());
        Assert.True(profile.GetProperty("vatSurplus").GetBoolean());
        Assert.True(profile.GetProperty("isPrincipal").GetBoolean());
        // Las tasas del perfil no son las del cliente.
        Assert.Empty(response.GetProperty("withholdingTaxRates").EnumerateArray());

        var read = await client.GetFromJsonAsync<JsonElement>(
            $"{CustomersUrl()}/{customer.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(profile.GetRawText(), Assert.Single(Profiles(read)).GetRawText());
        Assert.Equal(50, read.GetProperty("billingProfileLimit").GetInt32());
    }

    [Fact]
    public async Task PostingASpanishProfileReturnsItsRegion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var madrid = await MadridRegionIdAsync(client);

        var response = await ReadAsync(await PostAsync(client, customer.Id, SpanishBody(madrid)));

        var profile = Assert.Single(Profiles(response));
        Assert.Equal("ES", profile.GetProperty("country").GetString());
        Assert.Equal("Madrid", profile.GetProperty("cityName").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("cityId").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("departmentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("departmentName").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("personType").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("phone").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("email").ValueKind);
        Assert.Equal(madrid, profile.GetProperty("region").GetProperty("id").GetGuid());
        Assert.Equal("Madrid", profile.GetProperty("region").GetProperty("name").GetString());
        Assert.Empty(profile.GetProperty("withholdingTaxRates").EnumerateArray());
        Assert.False(profile.GetProperty("vatSurplus").GetBoolean());
    }

    // El frontend guarda el perfil después de grabar el documento: un reintento, con el número escrito de otra forma, no
    // duplica la fila ni sube la versión del cliente.
    [Fact]
    public async Task RepeatingThePostCreatesNoRowAndKeepsTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var first = await ReadAsync(await PostAsync(
            client, customer.Id, ColombianBody(city.CityId, identificationNumber: "900123456-X")));
        var versionSql = $"SELECT version FROM customers.customers WHERE id = '{customer.Id}'";
        var rowsSql = $"SELECT count(*) FROM customers.customer_billing_profiles WHERE customer_id = '{customer.Id}'";
        var version = await ScalarAsync(database.GetConnectionString(), versionSql);
        var retry = ColombianBody(city.CityId, name: "comercial andina sas", identificationNumber: "900.123.456-x");
        retry["phone"] = "3109999999";
        retry["vatSurplus"] = false;

        var second = await ReadAsync(await PostAsync(client, customer.Id, retry));

        var profile = Assert.Single(Profiles(second));
        Assert.Equal(Assert.Single(Profiles(first)).GetRawText(), profile.GetRawText());
        Assert.Equal(1, await ScalarAsync(database.GetConnectionString(), rowsSql));
        Assert.Equal(version, await ScalarAsync(database.GetConnectionString(), versionSql));
    }

    [Fact]
    public async Task MunicipalInactiveAndRepeatedRatesAreRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var connectionString = database.GetConnectionString();
        var municipal = await InsertTaxRateAsync(connectionString, "ReteICA Medellin", "ReteICA", 0.966m);
        var inactive = await InsertTaxRateAsync(
            connectionString, "Retefuente vieja", "Retefuente", 1m, isActive: false);
        var low = await InsertTaxRateAsync(connectionString, "Retefuente 2.5", "Retefuente", 2.5m);
        var high = await InsertTaxRateAsync(connectionString, "Retefuente 4", "Retefuente", 4m);

        foreach (var (rates, code) in new (Guid[], string)[]
        {
            ([municipal], "customers.customer.withholding_tax_rate_municipal"),
            ([inactive], "customers.customer.withholding_tax_rate_invalid"),
            ([low, high], "customers.customer.withholding_type_repeated")
        })
        {
            var body = ColombianBody(city.CityId);
            body["withholdingTaxRateIds"] = rates;

            var response = await PostAsync(client, customer.Id, body);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(code, await ProblemCodeAsync(response));
        }

        var read = await client.GetFromJsonAsync<JsonElement>(
            $"{CustomersUrl()}/{customer.Id}", TestContext.Current.CancellationToken);
        Assert.Empty(Profiles(read));
    }

    [Fact]
    public async Task PutReplacesTheProfile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var madrid = await MadridRegionIdAsync(client);
        var rate = await InsertTaxRateAsync(
            database.GetConnectionString(), "Retefuente servicios", "Retefuente", 2.5m);
        var body = SpanishBody(madrid);
        body["phone"] = "+34 600 111 222";
        body["withholdingTaxRateIds"] = new[] { rate };
        body["vatSurplus"] = true;
        var created = Assert.Single(Profiles(await ReadAsync(await PostAsync(client, customer.Id, body))));
        var profileId = created.GetProperty("id").GetGuid();
        Assert.Single(created.GetProperty("withholdingTaxRates").EnumerateArray());
        var replacement = SpanishBody(regionId: null, name: "Andina Europa SL");
        replacement.Remove("regionId");
        replacement["withholdingTaxRateIds"] = Array.Empty<Guid>();

        var response = await ReadAsync(await PutAsync(client, customer.Id, profileId, replacement));

        var updated = Assert.Single(Profiles(response));
        Assert.Equal(profileId, updated.GetProperty("id").GetGuid());
        Assert.Equal("Andina Europa SL", updated.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("region").ValueKind);
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("phone").ValueKind);
        Assert.Empty(updated.GetProperty("withholdingTaxRates").EnumerateArray());
        Assert.False(updated.GetProperty("vatSurplus").GetBoolean());
        // «false» no desmarca la principal.
        Assert.True(updated.GetProperty("isPrincipal").GetBoolean());

        var toColombian = ColombianBody(city.CityId);
        toColombian["withholdingTaxRateIds"] = new[] { rate };
        var colombian = Assert.Single(Profiles(
            await ReadAsync(await PutAsync(client, customer.Id, profileId, toColombian))));
        Assert.Equal(city.CityId, colombian.GetProperty("cityId").GetGuid());
        Assert.Equal(city.CityName, colombian.GetProperty("cityName").GetString());
        Assert.Single(colombian.GetProperty("withholdingTaxRates").EnumerateArray());

        var missing = await PutAsync(client, customer.Id, Guid.CreateVersion7(), toColombian);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("customers.billing_profile.not_found", await ProblemCodeAsync(missing));
    }

    [Fact]
    public async Task DeletingThePrincipalIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var created = await ReadAsync(await PostAsync(client, customer.Id, ColombianBody(city.CityId)));
        var profileId = Assert.Single(Profiles(created)).GetProperty("id").GetGuid();

        var response = await DeleteAsync(client, customer.Id, profileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("customers.billing_profile.principal_not_removable", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task PostingPrincipalMovesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var first = Assert.Single(Profiles(await ReadAsync(await PostAsync(
            client, customer.Id, ColombianBody(city.CityId, name: "Beta SAS"))))).GetProperty("id").GetGuid();
        await ReadAsync(await PostAsync(client, customer.Id, ColombianBody(city.CityId, name: "Zeta SAS")));
        var added = await ReadAsync(await PostAsync(
            client, customer.Id, ColombianBody(city.CityId, name: "Alfa SAS")));
        // La principal primero y el resto por nombre.
        Assert.Equal(
            ["Beta SAS", "Alfa SAS", "Zeta SAS"],
            Profiles(added).Select(profile => profile.GetProperty("name").GetString()));
        var zeta = Profiles(added)[2].GetProperty("id").GetGuid();

        var moved = await ReadAsync(await MakePrincipalAsync(client, customer.Id, zeta));

        Assert.Equal(
            ["Zeta SAS", "Alfa SAS", "Beta SAS"],
            Profiles(moved).Select(profile => profile.GetProperty("name").GetString()));
        Assert.Equal(
            [true, false, false],
            Profiles(moved).Select(profile => profile.GetProperty("isPrincipal").GetBoolean()));

        var removed = await ReadAsync(await DeleteAsync(client, customer.Id, first));
        Assert.Equal(
            ["Zeta SAS", "Alfa SAS"],
            Profiles(removed).Select(profile => profile.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task WithoutPermissionOrFromAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var profileId = Assert.Single(Profiles(await ReadAsync(await PostAsync(
            client, customer.Id, ColombianBody(city.CityId))))).GetProperty("id").GetGuid();
        using var reader = CreateClient(factory, SubjectId, TenantId, CustomersPermissions.CustomerRead);
        using var stranger = CreateClient(
            factory, OtherSubjectId, OtherTenantId,
            CustomersPermissions.CustomerRead, CustomersPermissions.CustomerManage);

        foreach (var caller in new[] { reader, stranger })
        {
            // Sin cuerpo válido a propósito en el POST: la autorización va antes que la validación.
            var responses = new[]
            {
                await PostAsync(caller, customer.Id, new Dictionary<string, object?>()),
                await PutAsync(caller, customer.Id, profileId, ColombianBody(city.CityId)),
                await DeleteAsync(caller, customer.Id, profileId),
                await MakePrincipalAsync(caller, customer.Id, profileId)
            };

            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        }
    }

    [Fact]
    public async Task AnUnknownCustomerIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);

        var response = await PostAsync(client, Guid.CreateVersion7(), ColombianBody(city.CityId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("customers.customer.not_found", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ACustomerWithoutProfilesAnswersAnEmptyList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);

        var read = await client.GetFromJsonAsync<JsonElement>(
            $"{CustomersUrl()}/{customer.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(JsonValueKind.Array, read.GetProperty("billingProfiles").ValueKind);
        Assert.Empty(Profiles(read));
        Assert.Equal(50, read.GetProperty("billingProfileLimit").GetInt32());
    }

    [Fact]
    public async Task ValidationErrorsCarryTheFieldMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var body = ColombianBody(city.CityId, identificationNumber: "");

        var response = await PostAsync(client, customer.Id, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await ProblemCodeAsync(response));
        Assert.Equal(["IdentificationNumber"], await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task AProfileRegionOfAnotherCountryIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var customer = await NewCustomerAsync(client, city);
        var body = SpanishBody(await MadridRegionIdAsync(client));
        body["country"] = "PT";
        body["identificationType"] = "TAX_ID";
        body["cityName"] = "Lisboa";

        var response = await PostAsync(client, customer.Id, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("customers.billing_profile.region_invalid", await ProblemCodeAsync(response));
        Assert.Equal(["RegionId"], await ValidationFieldsAsync(response));
    }
}
