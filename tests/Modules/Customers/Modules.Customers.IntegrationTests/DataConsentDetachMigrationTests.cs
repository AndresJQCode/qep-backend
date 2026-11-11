using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Borrar clientes (spec 2026-11-10): el consentimiento de datos es prueba legal y sobrevive al cliente, así que
/// <c>customers.data_consents</c> pierde su FK a <c>customers.customers</c> y gana dos columnas con la foto del titular.</summary>
public sealed class DataConsentDetachMigrationTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);

    private static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheConsentForeignKeyIsGone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        _ = factory.Services;

        var found = await ScalarAsync(
            database.GetConnectionString(),
            "SELECT 1 FROM pg_constraint WHERE conname = 'FK_data_consents_customers_customer_id'");

        Assert.Null(found);
    }

    [Fact]
    public async Task TheSnapshotColumnsExistAndAreNullable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        _ = factory.Services;

        var name = await ScalarAsync(
            database.GetConnectionString(),
            "SELECT character_maximum_length || ':' || is_nullable FROM information_schema.columns "
            + "WHERE table_schema = 'customers' AND table_name = 'data_consents' AND column_name = 'customer_name'");
        var phone = await ScalarAsync(
            database.GetConnectionString(),
            "SELECT character_maximum_length || ':' || is_nullable FROM information_schema.columns "
            + "WHERE table_schema = 'customers' AND table_name = 'data_consents' AND column_name = 'customer_phone_e164'");

        Assert.Equal("160:YES", name);
        Assert.Equal("16:YES", phone);
    }

    [Fact]
    public async Task AConsentSurvivesItsCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        _ = factory.Services;
        var connectionString = database.GetConnectionString();
        var customerId = Guid.CreateVersion7();
        await ScalarAsync(
            connectionString,
            """
            INSERT INTO customers.customers
                (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at, completeness)
            VALUES (@id, @tenant, 'Cliente', true, false, false, 1, now(), now(), 'Incomplete')
            """,
            ("id", customerId), ("tenant", Tenant));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var identity = new DataConsentIdentity(Guid.CreateVersion7(), DataConsentChannel.WhatsApp, "CO.123456789", "573001234567");
            var evidence = new ChatbotConsentEvidence(
                "¿Aceptas nuestra política de datos?", Guid.CreateVersion7(), Guid.CreateVersion7(), "wamid.HBgM",
                Guid.CreateVersion7(), "Bienvenida", Guid.CreateVersion7(), 3, "n4");
            scope.ServiceProvider.GetRequiredService<ICustomerDataConsentRepository>().Add(
                CustomerDataConsent.FromChatbot(
                    Tenant, new CustomerId(customerId), DataConsentEvent.Accepted, DateTimeOffset.UtcNow, identity, evidence));
            await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await ScalarAsync(connectionString, "DELETE FROM customers.customers WHERE id = @id", ("id", customerId));

        var remaining = await ScalarAsync(
            connectionString, "SELECT count(*) FROM customers.data_consents WHERE customer_id = @id", ("id", customerId));
        Assert.Equal(1L, remaining);
    }
}
