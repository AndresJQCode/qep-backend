using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Companies.Application;
using Modules.Companies.Infrastructure.Persistence;
using Npgsql;
using static Modules.Companies.IntegrationTests.CompaniesApiHarness;

namespace Modules.Companies.IntegrationTests;

/// <summary>
/// AddCompanyChargesVat (spec 2026-11-17 §5.1): las empresas que ya existen siguen cobrando IVA, y
/// un <c>false</c> guardado vuelve en <c>false</c>. Sin el sentinel del mapeo, EF omite el
/// <c>false</c> del INSERT y la base lo guarda en <c>true</c> (Review Focus 1 del plan).
/// </summary>
public sealed class CompanyChargesVatMigrationTests
{
    private const string LastMigrationBeforeChargesVat = "20261114232031_AddCompanyWithholdingTaxRates";

    [Fact]
    public async Task ExistingCompaniesChargeVatAfterTheMigration()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CompaniesDbContext>();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(LastMigrationBeforeChargesVat, TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var cityId = await ScalarAsync<Guid>(connection, "SELECT id FROM geography.cities LIMIT 1");
        var companyId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO companies.companies (id, tenant_id, name, tax_id, country, document_type, city_id, is_active, version, created_at, updated_at) " +
            "VALUES (@id, @tenant, 'Andes Logistica S.A.S.', '900123456', 'CO', 'NIT', @city, true, 1, now(), now())",
            connection))
        {
            insert.Parameters.AddWithValue("id", companyId);
            insert.Parameters.AddWithValue("tenant", Guid.Parse(TenantId));
            insert.Parameters.AddWithValue("city", cityId);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        await using var select = new NpgsqlCommand(
            "SELECT charges_vat FROM companies.companies WHERE id = @id", connection);
        select.Parameters.AddWithValue("id", companyId);
        Assert.True((bool)(await select.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task ChargesVatFalseSurvivesTheRoundTrip()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = Array.Empty<object>(),
                taxId = "900.111.222-3",
                country = "CO",
                documentType = "NIT",
                cityId,
                chargesVat = false,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var select = new NpgsqlCommand(
            "SELECT charges_vat FROM companies.companies WHERE id = @id", connection);
        select.Parameters.AddWithValue("id", company.Id);
        Assert.False((bool)(await select.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
