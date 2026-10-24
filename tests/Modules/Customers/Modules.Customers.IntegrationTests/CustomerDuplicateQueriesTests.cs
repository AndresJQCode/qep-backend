using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Persistence;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-24 §3.2 contra la base: la búsqueda por número normalizado y el índice de expresión que la sirve.</summary>
public sealed class CustomerDuplicateQueriesTests
{
    private const string IndexName = "IX_customers_tenant_identification_normalized";
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid OtherTenant = Guid.Parse(OtherTenantId);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> ColumnAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetValue(0).ToString()!);
        }

        return values;
    }

    // Por SQL: la prueba trata de la consulta, no de las reglas de alta. Incompleto basta para llevar un documento.
    private static Task InsertAsync(
        string connection, Guid tenant, Guid id, string name, string? type, string? number,
        DateTimeOffset createdAt, bool active = true, string completeness = "Incomplete") =>
        ExecuteAsync(
            connection,
            $"""
            INSERT INTO customers.customers
                (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at, completeness,
                 identification_type, identification_number)
            VALUES ('{id}', '{tenant}', '{name}', {(active ? "true" : "false")}, false, false, 1,
                    '{createdAt:O}', '{createdAt:O}', '{completeness}',
                    {(type is null ? "NULL" : $"'{type}'")}, {(number is null ? "NULL" : $"'{number}'")})
            """);

    private static async Task<IReadOnlyList<Domain.Customer>> FindAsync(
        QepApiFactory factory, Guid tenant, string normalized, Guid? exclude = null, int take = 20)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<ICustomerDuplicateQueries>();
        return await queries.FindByNormalizedIdentificationAsync(
            tenant, normalized, exclude, take, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheIndexIsTheNormalizedExpressionPartialOnTheNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);

        var definition = await ColumnAsync(
            database.GetConnectionString(),
            $"SELECT indexdef FROM pg_indexes WHERE schemaname = 'customers' AND indexname = '{IndexName}'");

        var indexDef = Assert.Single(definition);
        Assert.Contains("regexp_replace", indexDef);
        Assert.Contains("WHERE (identification_number IS NOT NULL)", indexDef);
    }

    [Fact]
    public async Task TheMatchQueryUsesTheNormalizedIndex()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        await InsertAsync(connection, Tenant, Guid.NewGuid(), "Uno", "Nit", "92-1396892", DateTimeOffset.UtcNow);

        // Con pocas filas el planificador prefiere el barrido; apagarlo deja ver si la expresión de la consulta es la del índice.
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", db))
        {
            await off.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var sql =
            $"EXPLAIN SELECT * FROM customers.customers WHERE tenant_id = '{Tenant}' AND identification_type IS NOT NULL " +
            $"AND identification_number IS NOT NULL AND {CustomerIdentificationSql.NormalizedNumber} = '921396892'";
        await using var explain = new NpgsqlCommand(sql, db);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                plan.Add(reader.GetString(0));
            }
        }

        Assert.Contains(plan, line => line.Contains(IndexName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMatchIgnoresPunctuationAndCaseAndKeepsTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var now = DateTimeOffset.UtcNow;
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        await InsertAsync(connection, Tenant, newer, "Nuevo", "Ce", "921396892", now);
        await InsertAsync(connection, Tenant, older, "Viejo", "Nit", "92-1396892", now.AddDays(-1));
        await InsertAsync(connection, OtherTenant, Guid.NewGuid(), "Ajeno", "Nit", "92 139 6892", now);

        var found = await FindAsync(factory, Tenant, "921396892");

        Assert.Equal([older, newer], found.Select(customer => customer.Id.Value));
    }

    [Fact]
    public async Task TheExcludedCustomerAndRowsWithoutDocumentDoNotCome()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var now = DateTimeOffset.UtcNow;
        var excluded = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await InsertAsync(connection, Tenant, excluded, "Excluido", "Nit", "123-4", now);
        await InsertAsync(connection, Tenant, kept, "Otro", "Cc", "1234", now.AddMinutes(1));
        // Sin tipo: un número suelto no identifica a nadie.
        await InsertAsync(connection, Tenant, Guid.NewGuid(), "SinTipo", null, "1234", now.AddMinutes(2));
        await InsertAsync(connection, Tenant, Guid.NewGuid(), "SinDocumento", null, null, now.AddMinutes(3));

        var found = await FindAsync(factory, Tenant, "1234", excluded);

        Assert.Equal(kept, Assert.Single(found).Id.Value);
    }

    [Fact]
    public async Task InactiveAndIncompleteOwnersCome()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var inactive = Guid.NewGuid();
        await InsertAsync(connection, Tenant, inactive, "Inactivo", "Nit", "555", DateTimeOffset.UtcNow, active: false);

        var found = await FindAsync(factory, Tenant, "555");

        Assert.Equal(inactive, Assert.Single(found).Id.Value);
    }

    [Fact]
    public async Task TheTakeCapsTheRows()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var now = DateTimeOffset.UtcNow;
        // El índice único exacto del tipo y número obliga a que cada fila difiera en la forma: sólo la puntuación cambia.
        for (var i = 0; i < 25; i++)
        {
            await InsertAsync(connection, Tenant, Guid.NewGuid(), $"Cliente {i}", "Nit", "777-1" + new string('.', i), now.AddSeconds(i));
        }

        var found = await FindAsync(factory, Tenant, "7771", take: 20);

        Assert.Equal(20, found.Count);
    }
}
