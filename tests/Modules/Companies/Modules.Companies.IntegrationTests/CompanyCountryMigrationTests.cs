using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Companies.Infrastructure.Persistence;
using Npgsql;
using static Modules.Companies.IntegrationTests.CompaniesApiHarness;

namespace Modules.Companies.IntegrationTests;

/// <summary>
/// AddCompanyCountryAndDocumentType rellena las empresas que ya existen como colombianas con NIT
/// (spec 2026-11-07, D6) y deja la ciudad intacta. El host migra todo al arrancar, asi que la
/// prueba retrocede Companies a la migracion anterior, inserta una fila cruda y vuelve a migrar.
/// </summary>
public sealed class CompanyCountryMigrationTests
{
    private const string LastMigrationBeforeCountry = "20260831004523_AddCompanyCity";

    [Fact]
    public async Task ExistingCompaniesBecomeColombianWithNitAndKeepTheirCity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CompaniesDbContext>();
        var migrator = context.GetService<IMigrator>();
        var countryMigration = context.Database.GetMigrations()
            .Single(id => id.EndsWith("_AddCompanyCountryAndDocumentType", StringComparison.Ordinal));

        await migrator.MigrateAsync(LastMigrationBeforeCountry, TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var cityId = await ScalarAsync<Guid>(connection, "SELECT id FROM geography.cities LIMIT 1");
        var companyId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO companies.companies (id, tenant_id, name, tax_id, city_id, is_active, version, created_at, updated_at) " +
            "VALUES (@id, @tenant, 'Andes Logistica S.A.S.', '900123456', @city, true, 1, now(), now())",
            connection))
        {
            insert.Parameters.AddWithValue("id", companyId);
            insert.Parameters.AddWithValue("tenant", Guid.Parse(TenantId));
            insert.Parameters.AddWithValue("city", cityId);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await migrator.MigrateAsync(countryMigration, TestContext.Current.CancellationToken);

        await using var select = new NpgsqlCommand(
            "SELECT country || '|' || document_type || '|' || city_id::text || '|' || (city_name IS NULL)::text " +
            "FROM companies.companies WHERE id = @id",
            connection);
        select.Parameters.AddWithValue("id", companyId);
        Assert.Equal($"CO|NIT|{cityId}|true",
            (string?)await select.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ACompanyInColombiaCannotCarryAFreeTextCityName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CompaniesDbContext>();
        await context.GetService<IMigrator>().MigrateAsync(null, TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var cityId = await ScalarAsync<Guid>(connection, "SELECT id FROM geography.cities LIMIT 1");
        await using var insert = new NpgsqlCommand(
            "INSERT INTO companies.companies (id, tenant_id, name, tax_id, country, document_type, city_id, city_name, is_active, version, created_at, updated_at) " +
            "VALUES (@id, @tenant, 'Andes', '900123456', 'CO', 'NIT', @city, 'Madrid', true, 1, now(), now())",
            connection);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("tenant", Guid.Parse(TenantId));
        insert.Parameters.AddWithValue("city", cityId);

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal("CK_companies_city_lane", failure.ConstraintName);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
