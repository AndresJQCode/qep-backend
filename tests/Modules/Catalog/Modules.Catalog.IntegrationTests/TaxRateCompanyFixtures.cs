using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Lo que las pruebas de «en uso» necesitan para que una empresa tenga una tasa municipal asignada (spec ReteICA por empresa
/// §7.2): la tasa se inserta en el catálogo por SQL y la empresa se crea por la API de Companies.
/// </summary>
internal static class TaxRateCompanyFixtures
{
    // Por SQL, como TaxRateCustomerFixtures: crear la ReteICA por HTTP exige una empresa CO previa en el tenant.
    public static async Task<Guid> CreateMunicipalRateAsync(string connectionString, string tenantId, string name)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO catalog.tax_rates
                (id, tenant_id, name, percentage, type, country, minimum_base, city_id, is_active, version, created_at, updated_at)
            VALUES (@id, @tenantId, @name, 0.966, 'ReteICA', 'CO', NULL,
                    (SELECT id FROM geography.cities ORDER BY divipola_code LIMIT 1), true, 1, now(), now())
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenantId", Guid.Parse(tenantId));
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>Una empresa colombiana con la tasa asignada. Ningún cliente ni producto la usa.</summary>
    public static async Task AssignToANewCompanyAsync(
        HttpClient companyManager, string connectionString, string tenantId, Guid taxRateId)
    {
        var response = await companyManager.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = new[] { new { bankName = "Bancolombia", accountNumber = "CTA-1", currency = "COP" } },
                taxId = "900.111.222-3",
                country = "CO",
                documentType = "NIT",
                cityId = await FirstCityIdAsync(connectionString),
                withholdingTaxRateIds = new[] { taxRateId },
            },
            TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<Guid> FirstCityIdAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id FROM geography.cities ORDER BY divipola_code LIMIT 1", connection);
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
