using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-24 §7: el documento deja de ser único entre todos los clientes y pasa a serlo entre los que no
/// están fusionados; un índice parcial sobre <c>merged_into_id</c> sirve la consulta «quiénes apuntan a este».</summary>
public sealed class CustomerMergeMigrationTests
{
    private static async Task<string> IndexDefinitionAsync(string connectionString, string indexName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'customers' AND indexname = @name", connection);
        command.Parameters.AddWithValue("name", indexName);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? string.Empty);
    }

    [Fact]
    public async Task TheIdentificationIndexIsPartialOnNotMerged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await EnsureCityAsync(client);

        var definition = await IndexDefinitionAsync(database.GetConnectionString(), "IX_customers_tenant_identification");

        Assert.Contains("UNIQUE", definition);
        Assert.Contains("WHERE (merged_into_id IS NULL)", definition);
    }

    [Fact]
    public async Task TheMergedIntoIndexExistsAndIsPartial()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await EnsureCityAsync(client);

        var definition = await IndexDefinitionAsync(database.GetConnectionString(), "IX_customers_tenant_merged_into");

        Assert.Contains("(tenant_id, merged_into_id)", definition);
        Assert.Contains("WHERE (merged_into_id IS NOT NULL)", definition);
    }
}
