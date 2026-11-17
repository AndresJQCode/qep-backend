using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Las tasas de retención asignadas viven en <c>customers.customer_tax_rates</c>, sin FK a <c>catalog</c>. La columna
/// <c>with_retention</c> sigue en <c>customers.customers</c> como sombra hasta la migración de contrato (spec §11.5).
/// </summary>
public sealed class CustomerTaxRatePersistenceTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RateA = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid RateB = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    private static QepApiFactory Start(string connectionString)
    {
        var factory = new QepApiFactory(connectionString);
        _ = factory.Services;
        return factory;
    }

    private static Customer NewCustomer() =>
        Customer.CreateIncompleteFromInstagram(CustomerId.New(), Tenant, "Cliente", "17841400000001", null, Now);

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task RatesRoundTrip()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = Start(database.GetConnectionString());
        var customer = NewCustomer();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            customer.SetWithholdingTaxRates([RateA, RateB], Now);
            scope.ServiceProvider.GetRequiredService<ICustomerRepository>().Add(customer);
            await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
            var loaded = await repository.FindAsync(Tenant, customer.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(loaded);
            Assert.Equivalent(new[] { RateA, RateB }, loaded.WithholdingTaxRateIds);

            loaded.SetWithholdingTaxRates([RateB], Now.AddMinutes(1));
            await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            1,
            await CountAsync(
                database.GetConnectionString(),
                $"SELECT count(*) FROM customers.customer_tax_rates WHERE customer_id = '{customer.Id.Value}' "
                + $"AND tax_rate_id = '{RateB}' AND tenant_id = '{Tenant}'"));
        Assert.Equal(
            1,
            await CountAsync(
                database.GetConnectionString(),
                $"SELECT count(*) FROM customers.customer_tax_rates WHERE customer_id = '{customer.Id.Value}'"));
    }

    [Fact]
    public async Task ANewCustomerInsertsWithRetentionFalse()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = Start(database.GetConnectionString());
        var customer = NewCustomer();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ICustomerRepository>().Add(customer);
            await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            1,
            await CountAsync(
                database.GetConnectionString(),
                $"SELECT count(*) FROM customers.customers WHERE id = '{customer.Id.Value}' AND with_retention = false"));
    }
}
