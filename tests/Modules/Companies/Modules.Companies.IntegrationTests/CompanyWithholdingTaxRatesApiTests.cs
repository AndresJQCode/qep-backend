using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Npgsql;
using static Modules.Companies.IntegrationTests.CompaniesApiHarness;

namespace Modules.Companies.IntegrationTests;

/// <summary>
/// Spec ReteICA por empresa §7.3 y §9.2: la empresa facturadora guarda sus tasas municipales, una por municipio y tipo, y la
/// ficha las devuelve con el municipio resuelto. Las tasas se crean por la API de Catalog, con municipios reales de Geography.
/// </summary>
public sealed class CompanyWithholdingTaxRatesApiTests
{
    private sealed record GeographyDepartment(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCity(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    private sealed record Municipality(Guid Id, string Name, string DepartmentName);

    private static HttpClient CreateAdmin(QepApiFactory factory) =>
        CreateClient(
            factory,
            SubjectId,
            TenantId,
            CompaniesPermissions.CompanyRead,
            CompaniesPermissions.CompanyManage,
            CatalogPermissions.TaxRateRead,
            CatalogPermissions.TaxRateManage);

    [Fact]
    public async Task AssigningBogotaAndMedellinPasses()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, second) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var firstRate = await CreateRateAsync(client, "ReteICA uno", "ReteICA", first.Id);
        var secondRate = await CreateRateAsync(client, "ReteICA dos", "ReteICA", second.Id);

        var response = await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { firstRate, secondRate });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rates = (await ReadAsync(response)).WithholdingTaxRates;
        Assert.Equal(2, rates.Count);
        Assert.Equal(new HashSet<Guid> { firstRate, secondRate }, rates.Select(rate => rate.Id).ToHashSet());
        foreach (var (rate, municipality) in new[] { (firstRate, first), (secondRate, second) })
        {
            var dto = rates.Single(value => value.Id == rate);
            Assert.Equal(municipality.Name, dto.City?.Name);
            Assert.Equal(municipality.DepartmentName, dto.City?.DepartmentName);
            Assert.Equal(0.966m, dto.Percentage);
            Assert.True(dto.IsActive);
            Assert.Equal("ReteICA", dto.Type);
            Assert.Equal("CO", dto.Country);
        }
    }

    [Fact]
    public async Task TwoInBogotaAre422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var one = await CreateRateAsync(client, "ReteICA comercio", "ReteICA", first.Id);
        var two = await CreateRateAsync(client, "ReteICA servicios", "ReteICA", first.Id);

        var response = await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { one, two });

        await AssertCodeAsync(response, "companies.company.withholding_municipality_repeated");
    }

    // Spec 2026-11-18 §4.3: una ReteICA es una por municipio, cualquiera sea su alcance. Las tasas se crean por la API de
    // Catalog con su alcance.
    [Fact]
    public async Task CompanyRejectsGoodsAndServicesInTheSameMunicipality()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var goods = await CreateRateAsync(client, "ReteICA comercio", "ReteICA", first.Id, appliesTo: "Goods");
        var services = await CreateRateAsync(client, "ReteICA servicios", "ReteICA", first.Id, appliesTo: "Services");

        var response = await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { goods, services });

        await AssertCodeAsync(response, "companies.company.withholding_municipality_repeated");
    }

    [Fact]
    public async Task CompanyRejectsAllWithAnotherScope()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var all = await CreateRateAsync(client, "ReteICA general", "ReteICA", first.Id);
        var services = await CreateRateAsync(client, "ReteICA servicios", "ReteICA", first.Id, appliesTo: "Services");

        var response = await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { all, services });

        await AssertCodeAsync(response, "companies.company.withholding_municipality_repeated");
    }

    // Spec 2026-11-18 servicios pendientes §2: la tasa guardada trae su alcance, así la ficha dice «ReteICA (servicios)»
    // aunque la tasa ya no esté en el catálogo activo. Se lee como JSON: el contrato que se prueba es el de la red.
    [Fact]
    public async Task SavedRateCarriesItsScope()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var services = await CreateRateAsync(client, "ReteICA servicios", "ReteICA", first.Id, appliesTo: "Services");
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { services })).StatusCode);

        var detail = await client.GetStringAsync($"{CompaniesUrl()}/{company.Id}", TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(detail);
        var rate = Assert.Single(document.RootElement.GetProperty("withholdingTaxRates").EnumerateArray());
        Assert.Equal(services, rate.GetProperty("id").GetGuid());
        Assert.Equal("Services", rate.GetProperty("appliesTo").GetString());
    }

    [Fact]
    public async Task RetefuenteIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var retefuente = await CreateRateAsync(client, "Retefuente 2,5", "Retefuente", null);

        var response = await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { retefuente });

        await AssertCodeAsync(response, "companies.company.withholding_tax_rate_invalid");
    }

    // §9.2: ausente = conservar. El frontend viejo manda el PUT sin el campo y no puede borrar lo asignado.
    [Fact]
    public async Task PutWithoutTheFieldKeepsThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, second) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var firstRate = await CreateRateAsync(client, "ReteICA uno", "ReteICA", first.Id);
        var secondRate = await CreateRateAsync(client, "ReteICA dos", "ReteICA", second.Id);
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { firstRate, secondRate })).StatusCode);

        var response = await PutAsync(client, company.Id, first.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await ReadAsync(response)).WithholdingTaxRates.Count);
    }

    [Fact]
    public async Task EmptyListClearsThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var rate = await CreateRateAsync(client, "ReteICA uno", "ReteICA", first.Id);
        await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { rate });

        var response = await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: Array.Empty<Guid>());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ReadAsync(response)).WithholdingTaxRates);
    }

    // El listado no cambia (§9.2): la grilla no pinta las tasas y mandarlas engordaría cada fila.
    [Fact]
    public async Task GetReturnsThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var rate = await CreateRateAsync(client, "ReteICA uno", "ReteICA", first.Id);
        await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { rate });

        var detail = await client.GetFromJsonAsync<CompanyResponse>(
            $"{CompaniesUrl()}/{company.Id}", TestContext.Current.CancellationToken);
        var list = await client.GetStringAsync(CompaniesUrl(), TestContext.Current.CancellationToken);

        Assert.Equal(rate, Assert.Single(detail!.WithholdingTaxRates).Id);
        using var document = JsonDocument.Parse(list);
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(item.TryGetProperty("withholdingTaxRates", out _));
    }

    // A16: desactivar una tasa asignada lo frena Catalog con in_use, así que se desactiva por SQL para simular la carrera.
    [Fact]
    public async Task ResendingAnAlreadyAssignedInactiveRatePasses()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var rate = await CreateRateAsync(client, "ReteICA uno", "ReteICA", first.Id);
        await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { rate });
        await DeactivateBySqlAsync(database.GetConnectionString(), rate);

        var response = await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { rate });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var kept = Assert.Single((await ReadAsync(response)).WithholdingTaxRates);
        Assert.Equal(rate, kept.Id);
        Assert.False(kept.IsActive);
    }

    [Fact]
    public async Task ANewInactiveRateIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var rate = await CreateRateAsync(client, "ReteICA uno", "ReteICA", first.Id);
        await DeactivateBySqlAsync(database.GetConnectionString(), rate);

        var response = await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { rate });

        await AssertCodeAsync(response, "companies.company.withholding_tax_rate_invalid");
    }

    // La lista resultante (la guardada, si el cuerpo no la trae) se valida contra el país nuevo.
    [Fact]
    public async Task ChangingCountryRevalidatesStoredRates()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        var company = await CreateCompanyAsync(client, "Andes", "CTA-1", cityId: first.Id);
        var rate = await CreateRateAsync(client, "ReteICA uno", "ReteICA", first.Id);
        await PutAsync(client, company.Id, first.Id, withholdingTaxRateIds: new[] { rate });

        var response = await client.PutAsJsonAsync(
            $"{CompaniesUrl()}/{company.Id}",
            new
            {
                name = "Andes",
                bankAccounts = new[] { BankAccount("CTA-1", currency: "EUR") },
                taxId = "B12345678",
                country = "ES",
                documentType = "NIF",
                cityName = "Madrid",
            },
            TestContext.Current.CancellationToken);

        await AssertCodeAsync(response, "companies.company.withholding_tax_rate_invalid");
    }

    [Fact]
    public async Task CreateWithRatesAssignsThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var (first, _) = await TwoMunicipalitiesAsync(client);
        // La tasa necesita una empresa CO en el tenant (country_not_available), así que la primera empresa va sin tasas.
        await CreateCompanyAsync(client, "Primera", "CTA-0", cityId: first.Id);
        var rate = await CreateRateAsync(client, "ReteICA uno", "ReteICA", first.Id);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes",
                bankAccounts = new[] { BankAccount("CTA-1") },
                taxId = "900.111.222-3",
                country = "CO",
                documentType = "NIT",
                cityId = first.Id,
                withholdingTaxRateIds = new[] { rate },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(rate, Assert.Single((await ReadAsync(response)).WithholdingTaxRates).Id);
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, Guid companyId, Guid cityId, IReadOnlyList<Guid>? withholdingTaxRateIds = null)
    {
        object body = withholdingTaxRateIds is null
            ? new
            {
                name = "Andes",
                bankAccounts = new[] { BankAccount("CTA-1") },
                taxId = "900.111.222-3",
                country = "CO",
                documentType = "NIT",
                cityId,
            }
            : new
            {
                name = "Andes",
                bankAccounts = new[] { BankAccount("CTA-1") },
                taxId = "900.111.222-3",
                country = "CO",
                documentType = "NIT",
                cityId,
                withholdingTaxRateIds,
            };
        return client.PutAsJsonAsync($"{CompaniesUrl()}/{companyId}", body, TestContext.Current.CancellationToken);
    }

    private static async Task<CompanyResponse> ReadAsync(HttpResponseMessage response)
    {
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        return company;
    }

    private static async Task<Guid> CreateRateAsync(
        HttpClient client, string name, string type, Guid? cityId, string appliesTo = "All")
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates",
            new { name, percentage = 0.966m, type, country = "CO", cityId, appliesTo },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task DeactivateBySqlAsync(string connectionString, Guid taxRateId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE catalog.tax_rates SET is_active = false WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", taxRateId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static async Task AssertCodeAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    // Dos municipios reales de la DIVIPOLA sembrada, de un mismo departamento: los nombres salen de Geography.
    private static async Task<(Municipality First, Municipality Second)> TwoMunicipalitiesAsync(HttpClient client)
    {
        var departments = await client.GetFromJsonAsync<List<GeographyDepartment>>(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        foreach (var department in departments!)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyCity>>(
                $"/api/v1/cities?departmentId={department.Id}", TestContext.Current.CancellationToken);
            if (cities is { Count: >= 2 })
            {
                return (
                    new Municipality(cities[0].Id, cities[0].Name, department.Name),
                    new Municipality(cities[1].Id, cities[1].Name, department.Name));
            }
        }

        throw new InvalidOperationException("No seeded department has two cities.");
    }
}
