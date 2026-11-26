using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Lo que las pruebas de «en uso» necesitan para que un cliente tenga una tasa de retención asignada: la tasa se inserta en
/// el catálogo por SQL y el cliente se crea por la API de Customers.
/// </summary>
internal static class TaxRateCustomerFixtures
{
    // Por SQL y no por la API: crear una Retefuente por HTTP exige una empresa del tenant con ese país, y esa cadena no es lo
    // que estas pruebas verifican.
    public static async Task<Guid> CreateWithholdingRateAsync(string connectionString, string tenantId, string name)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO catalog.tax_rates
                (id, tenant_id, name, percentage, type, country, minimum_base, is_active, version, created_at, updated_at)
            VALUES (@id, @tenantId, @name, 4, 'Retefuente', 'CO', NULL, true, 1, now(), now())
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenantId", Guid.Parse(tenantId));
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>Un cliente de afuera (sin ciudad DIVIPOLA que sembrar) con la tasa asignada.</summary>
    public static async Task AssignToANewCustomerAsync(HttpClient customerManager, string tenantId, Guid taxRateId)
    {
        var response = await customerManager.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers",
            new
            {
                name = "Verde Esencial S.L.",
                identificationType = "NIF",
                identificationNumber = "B-12345678",
                phone = "+34 910 000 000",
                email = "compras@verde.es",
                address = "Calle Gran Via 28",
                country = "ES",
                cityName = "Madrid",
                withholdingTaxRateIds = new[] { taxRateId }
            },
            TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Un cliente de afuera **sin** tasas propias y con un perfil de facturación que sí la tiene. El perfil se guarda por el
    /// repositorio: lo que se verifica es la consulta del candado, no la API de Customers.
    /// </summary>
    public static async Task AssignToABillingProfileOfANewCustomerAsync(
        HttpClient customerManager, IServiceProvider services, string tenantId, Guid taxRateId)
    {
        var response = await customerManager.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers",
            new
            {
                name = "Casa Verde S.L.",
                identificationType = "NIF",
                identificationNumber = "B-87654321",
                phone = "+34 910 000 001",
                email = "compras@casaverde.es",
                address = "Calle Mayor 1",
                country = "ES",
                cityName = "Madrid"
            },
            TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var customerId = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();

        await using var scope = services.CreateAsyncScope();
        var customer = await scope.ServiceProvider.GetRequiredService<ICustomerRepository>().FindAsync(
            Guid.Parse(tenantId), new CustomerId(customerId), TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        customer.EnsureBillingProfile(
            new CustomerBillingProfileDetails
            {
                Name = "Casa Verde S.L.",
                IdentificationType = "NIF",
                IdentificationNumber = "B-87654321",
                Address = "Calle Mayor 1",
                Country = "ES",
                CityName = "Madrid",
                WithholdingTaxRateIds = [taxRateId]
            },
            false,
            DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Deja fusionados a los clientes que tienen la tasa, como lo haría la fusión (que no les quita sus tasas). Por SQL: la
    /// fusión por la API exige un sobreviviente con sus propios datos, y eso no es lo que estas pruebas verifican.
    /// </summary>
    public static async Task MergeCustomersWithAsync(string connectionString, Guid taxRateId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SET session_replication_role = replica;
            UPDATE customers.customers
            SET merged_into_id = gen_random_uuid(), merged_at = now(), is_active = false
            WHERE id IN (SELECT customer_id FROM customers.customer_tax_rates WHERE tax_rate_id = @taxRateId);
            RESET session_replication_role;
            """,
            connection);
        command.Parameters.AddWithValue("taxRateId", taxRateId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }
}
