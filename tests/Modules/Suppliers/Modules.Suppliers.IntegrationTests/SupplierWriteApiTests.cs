using System.Net;
using System.Net.Http.Json;
using Modules.Suppliers.Application;
using Npgsql;
using static Modules.Suppliers.IntegrationTests.SuppliersApiHarness;

namespace Modules.Suppliers.IntegrationTests;

public sealed class SupplierWriteApiTests
{
    [Fact]
    public async Task CreateReturns201WithLocationAndTheFullDetail()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);

        var response = await client.PostAsJsonAsync(
            SuppliersUrl(), ValidBody(cityId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var supplier = await response.Content.ReadFromJsonAsync<SupplierResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(supplier);
        Assert.EndsWith(
            $"/suppliers/{supplier.Id}", response.Headers.Location?.ToString(), StringComparison.Ordinal);
        Assert.NotNull(supplier.City);
        Assert.Equal(cityId, supplier.City.Id);
        Assert.NotNull(supplier.Department);
        Assert.Null(supplier.CityName);
        Assert.True(supplier.IsActive);
        Assert.Equal("NIT", supplier.IdentificationType);
        Assert.Equal(["123-456", "789-012"], supplier.BankAccounts.Select(account => account.AccountNumber));
        Assert.Equal(["COP", "USD"], supplier.BankAccounts.Select(account => account.Currency));
        Assert.Equal(["Marta Rojas", "Luis Pardo"], supplier.Contacts.Select(contact => contact.Name));
    }

    // D-4: teléfono, correo y dirección son opcionales.
    [Fact]
    public async Task CreateWithoutPhoneEmailAndAddressReturns201WithNulls()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var supplier = await CreateSupplierAsync(client, new()
        {
            ["phone"] = null,
            ["email"] = null,
            ["address"] = null
        });

        Assert.Null(supplier.Phone);
        Assert.Null(supplier.Email);
        Assert.Null(supplier.Address);
    }

    [Fact]
    public async Task CreateOutsideColombiaKeepsCityNameAndReturnsNullCityAndDepartment()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var supplier = await CreateSupplierAsync(client, new()
        {
            ["country"] = "US",
            ["cityId"] = null,
            ["cityName"] = "Miami",
            ["phone"] = null
        });

        Assert.Equal("US", supplier.Country);
        Assert.Equal("Miami", supplier.CityName);
        Assert.Null(supplier.City);
        Assert.Null(supplier.Department);
    }

    [Fact]
    public async Task CreateWithUnknownCityIdIs422CityNotFound()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            SuppliersUrl(), ValidBody(Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("suppliers.supplier.city_not_found", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWithAnUnknownIdentificationTypeIs422ValidationFailedOnIdentificationType()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);

        var response = await client.PostAsJsonAsync(
            SuppliersUrl(),
            ValidBody(cityId, new() { ["identificationType"] = "RUT" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("validation.failed", await BodyAsync(response), StringComparison.Ordinal);
        Assert.Contains("IdentificationType", await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task CreateWithADuplicatedBankAccountIs422OnTheRow()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);

        var response = await client.PostAsJsonAsync(
            SuppliersUrl(),
            ValidBody(cityId, new()
            {
                ["bankAccounts"] = new object[]
                {
                    new { bankName = "Bancolombia", accountNumber = "123-456", currency = "COP" },
                    new { bankName = "Bancolombia", accountNumber = "123-456", currency = "COP" }
                }
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("BankAccounts[1].AccountNumber", await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task CreateWithAContactWithoutNameIs422OnTheRow()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);

        var response = await client.PostAsJsonAsync(
            SuppliersUrl(),
            ValidBody(cityId, new()
            {
                ["contacts"] = new object[] { new { name = "", role = "Ventas" } }
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Contacts[0].Name", await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task CreateWithTheSameDocumentIs422IdentificationTaken()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateSupplierAsync(client);
        var cityId = await EnsureCityIdAsync(client);

        var response = await client.PostAsJsonAsync(
            SuppliersUrl(),
            ValidBody(cityId, new() { ["name"] = "Otro nombre" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "suppliers.supplier.identification_taken", await BodyAsync(response), StringComparison.Ordinal);
    }

    // Review Focus 1: el documento es único también entre inactivos (D-5).
    [Fact]
    public async Task CreatingWithTheDocumentOfAnInactiveSupplierIs422IdentificationTaken()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var existing = await CreateSupplierAsync(client);
        (await client.PostAsync(
            $"{SuppliersUrl()}/{existing.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var cityId = await EnsureCityIdAsync(client);

        var response = await client.PostAsJsonAsync(
            SuppliersUrl(), ValidBody(cityId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "suppliers.supplier.identification_taken", await BodyAsync(response), StringComparison.Ordinal);
    }

    // Review Focus 2: no hay pre-chequeo; el índice único es la única barrera y el UnitOfWork
    // traduce su violación.
    [Fact]
    public async Task TwoConcurrentCreatesWithTheSameDocumentLeaveOneSupplier()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                SuppliersUrl(), ValidBody(cityId), TestContext.Current.CancellationToken),
            client.PostAsJsonAsync(
                SuppliersUrl(), ValidBody(cityId), TestContext.Current.CancellationToken));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        var rejected = Assert.Single(
            responses, response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Contains(
            "suppliers.supplier.identification_taken", await BodyAsync(rejected), StringComparison.Ordinal);

        var page = await client.GetFromJsonAsync<SuppliersResponse>(
            SuppliersUrl(), TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task UpdateReplacesTheResourceAndClearsOptionalsSentAsNull()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateSupplierAsync(client);
        var cityId = created.City!.Id;

        var response = await client.PutAsJsonAsync(
            $"{SuppliersUrl()}/{created.Id}",
            ValidBody(cityId, new()
            {
                ["name"] = "Nuevo nombre",
                ["businessName"] = null,
                ["phone"] = null,
                ["email"] = null,
                ["address"] = null,
                ["category"] = null,
                ["notes"] = null,
                ["bankAccounts"] = Array.Empty<object>(),
                ["contacts"] = Array.Empty<object>()
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var supplier = await response.Content.ReadFromJsonAsync<SupplierResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(supplier);
        Assert.Equal("Nuevo nombre", supplier.Name);
        Assert.Null(supplier.BusinessName);
        Assert.Null(supplier.Phone);
        Assert.Null(supplier.Email);
        Assert.Null(supplier.Address);
        Assert.Null(supplier.Category);
        Assert.Null(supplier.Notes);
        Assert.Empty(supplier.BankAccounts);
        Assert.Empty(supplier.Contacts);
    }

    // Review Focus 3.
    [Fact]
    public async Task UpdatingFromColombiaToAnotherCountryClearsTheCityId()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateSupplierAsync(client);
        Assert.NotNull(created.City);

        var response = await client.PutAsJsonAsync(
            $"{SuppliersUrl()}/{created.Id}",
            ValidBody(created.City.Id, new()
            {
                ["country"] = "US",
                ["cityName"] = "Miami",
                ["phone"] = null
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var supplier = await response.Content.ReadFromJsonAsync<SupplierResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(supplier);
        Assert.Equal("US", supplier.Country);
        Assert.Null(supplier.City);
        Assert.Null(supplier.Department);
        Assert.Equal("Miami", supplier.CityName);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM suppliers.suppliers WHERE id = @id AND city_id IS NULL", connection);
        command.Parameters.AddWithValue("id", created.Id);
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateOfAnotherTenantsSupplierIs403AuthorizationDenied()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateSupplierAsync(client);

        var response = await client.PutAsJsonAsync(
            $"{SuppliersUrl(OtherTenantId)}/{created.Id}",
            ValidBody(created.City!.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("authorization.denied", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReaderCannotCreateOrUpdate()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var created = await CreateSupplierAsync(manager);
        using var reader = CreateReader(factory);

        var post = await reader.PostAsJsonAsync(
            SuppliersUrl(), ValidBody(created.City!.Id), TestContext.Current.CancellationToken);
        var put = await reader.PutAsJsonAsync(
            $"{SuppliersUrl()}/{created.Id}",
            ValidBody(created.City.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }
}
