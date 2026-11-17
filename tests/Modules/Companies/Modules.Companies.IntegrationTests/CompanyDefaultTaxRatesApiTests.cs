using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Tenancy.Application;
using Npgsql;
using static Modules.Companies.IntegrationTests.CompaniesApiHarness;

namespace Modules.Companies.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 §5.3 (R6): la primera empresa colombiana que cobra IVA siembra «IVA 19 %», «IVA 5 %» y «Exento», y fija
/// la del envío si el tenant no tiene. Cada prueba levanta su host: lee <c>catalog.tax_rates</c> y
/// <c>quotations.tenant_quotation_settings</c> del tenant, que es estado de todo el tenant.
/// </summary>
public sealed class CompanyDefaultTaxRatesApiTests
{
    private sealed record SeededRate(Guid Id, string Name, decimal Percentage, string Type, string Country, bool IsActive);

    [Fact]
    public async Task TwoColombianCompaniesCreateThreeRatesNotSix()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var cityId = await EnsureCityIdAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await PostCompanyAsync(client, "Andes", cityId, chargesVat: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostCompanyAsync(client, "Pacifico", cityId, chargesVat: true)).StatusCode);

        var rates = await RatesAsync(database.GetConnectionString());
        Assert.Equal(3, rates.Count);
        Assert.All(rates, rate =>
        {
            Assert.Equal("IVA", rate.Type);
            Assert.Equal("CO", rate.Country);
            Assert.True(rate.IsActive);
        });
        Assert.Equal(
            new HashSet<(string, decimal)> { ("IVA 19 %", 19m), ("IVA 5 %", 5m), ("Exento", 0m) },
            rates.Select(rate => (rate.Name, rate.Percentage)).ToHashSet());
    }

    [Fact]
    public async Task TheFreightRateIsTheNineteenPercentOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var cityId = await EnsureCityIdAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await PostCompanyAsync(client, "Andes", cityId, chargesVat: true)).StatusCode);

        var nineteen = (await RatesAsync(database.GetConnectionString())).Single(rate => rate.Name == "IVA 19 %");
        Assert.Equal(nineteen.Id, await FreightTaxRateIdAsync(database.GetConnectionString()));
    }

    [Fact]
    public async Task AnExistingFreightRateIsNotReplaced()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var cityId = await EnsureCityIdAsync(client);
        // Una empresa sin IVA no siembra, pero habilita CO en el tenant para crear la tasa a mano.
        Assert.Equal(HttpStatusCode.Created, (await PostCompanyAsync(client, "Sin IVA", cityId, chargesVat: false)).StatusCode);
        var eight = await CreateRateAsync(client, "IVA 8 %", 8m);
        var set = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/carriers/freight-tax-rate",
            new { taxRateId = eight },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        // Inactiva, para que la siguiente empresa sí siembre: lo que se prueba es que la del envío no se pise.
        await DeactivateBySqlAsync(database.GetConnectionString(), eight);

        Assert.Equal(HttpStatusCode.Created, (await PostCompanyAsync(client, "Andes", cityId, chargesVat: true)).StatusCode);

        Assert.Equal(4, (await RatesAsync(database.GetConnectionString())).Count);
        Assert.Equal(eight, await FreightTaxRateIdAsync(database.GetConnectionString()));
    }

    [Fact]
    public async Task SeedingSkipsAnExistingRateWithTheSameNameAndPercentage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var cityId = await EnsureCityIdAsync(client);
        Assert.Equal(HttpStatusCode.Created, (await PostCompanyAsync(client, "Sin IVA", cityId, chargesVat: false)).StatusCode);
        var inactive = await CreateRateAsync(client, "IVA 19 %", 19m);
        await DeactivateBySqlAsync(database.GetConnectionString(), inactive);

        var response = await PostCompanyAsync(client, "Andes", cityId, chargesVat: true);

        // Sin el salto, el INSERT chocaría con IX_tax_rates_tenant_name_percentage_base; la empresa se crea igual.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var rates = await RatesAsync(database.GetConnectionString());
        Assert.Equal(3, rates.Count);
        Assert.False(rates.Single(rate => rate.Id == inactive).IsActive);
        Assert.Contains(rates, rate => rate is { Name: "IVA 5 %", Percentage: 5m, IsActive: true });
        Assert.Contains(rates, rate => rate is { Name: "Exento", Percentage: 0m, IsActive: true });
        // No hay 19 % activa para el envío: queda vacía, como antes.
        Assert.Null(await FreightTaxRateIdAsync(database.GetConnectionString()));
    }

    [Fact]
    public async Task ConcurrentCompaniesStillCreateThreeRates()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdmin(factory);
        var cityId = await EnsureCityIdAsync(client);

        var responses = await Task.WhenAll(
            PostCompanyAsync(client, "Andes", cityId, chargesVat: true),
            PostCompanyAsync(client, "Pacifico", cityId, chargesVat: true));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(3, (await RatesAsync(database.GetConnectionString())).Count);
    }

    private static HttpClient CreateAdmin(QepApiFactory factory) =>
        CreateClient(
            factory,
            SubjectId,
            TenantId,
            CompaniesPermissions.CompanyRead,
            CompaniesPermissions.CompanyManage,
            CatalogPermissions.TaxRateRead,
            CatalogPermissions.TaxRateManage,
            TenancyPermissions.SettingsRead,
            TenancyPermissions.SettingsUpdate);

    private static Task<HttpResponseMessage> PostCompanyAsync(HttpClient client, string name, Guid cityId, bool? chargesVat)
    {
        object body = chargesVat is null
            ? new
            {
                name,
                bankAccounts = Array.Empty<object>(),
                taxId = "900.111.222-3",
                country = "CO",
                documentType = "NIT",
                cityId,
            }
            : new
            {
                name,
                bankAccounts = Array.Empty<object>(),
                taxId = "900.111.222-3",
                country = "CO",
                documentType = "NIT",
                cityId,
                chargesVat,
            };
        return client.PostAsJsonAsync(CompaniesUrl(), body, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> CreateRateAsync(HttpClient client, string name, decimal percentage)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates",
            new { name, percentage, type = "IVA", country = "CO" },
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

    private static async Task<List<SeededRate>> RatesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id, name, percentage, type, country, is_active FROM catalog.tax_rates WHERE tenant_id = @tenant",
            connection);
        command.Parameters.AddWithValue("tenant", Guid.Parse(TenantId));
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rates = new List<SeededRate>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rates.Add(new SeededRate(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetString(3),
                reader.GetString(4).Trim(),
                reader.GetBoolean(5)));
        }

        return rates;
    }

    private static async Task<Guid?> FreightTaxRateIdAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT freight_tax_rate_id FROM quotations.tenant_quotation_settings WHERE tenant_id = @tenant",
            connection);
        command.Parameters.AddWithValue("tenant", Guid.Parse(TenantId));
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is Guid id ? id : null;
    }
}
