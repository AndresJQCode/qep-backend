using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.2, §8.3 y §9.4 contra la base: crear el incompleto (con auditoría), vincular por
/// teléfono sin pisar otro BSUID (D-A6), la carrera de dos entregas del mismo BSUID (Review Focus RF9), el
/// reemplazo de BSUID y las lecturas por id y por nombre.</summary>
public sealed class CustomerWhatsAppDirectoryTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);

    private static async Task<T> InScopeAsync<T>(QepApiFactory factory, Func<ICustomerWhatsAppDirectory, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>());
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task ANewBsuidCreatesAnIncompleteCustomerOnceAndAuditsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var contact = new WhatsAppContact("CO.1349120865530274", "+573001234567", "Laura Pérez", "laura.p");

        var first = await InScopeAsync(factory, directory => directory.EnsureAsync(Tenant, contact, TestContext.Current.CancellationToken));
        var again = await InScopeAsync(factory, directory => directory.EnsureAsync(Tenant, contact, TestContext.Current.CancellationToken));

        Assert.Equal(EnsureOutcome.Created, first.Outcome);
        Assert.Equal(new EnsuredCustomer(first.CustomerId, EnsureOutcome.Existing), again);
        Assert.Equal("Incomplete|Laura Pérez|CO|+573001234567|CO.1349120865530274", await ScalarAsync(database.GetConnectionString(),
            $"SELECT completeness || '|' || name || '|' || country || '|' || phone_e164 || '|' || whatsapp_user_id FROM customers.customers WHERE id = '{first.CustomerId}'"));
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(),
            $"SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = '{CustomerAuditActions.CreatedFromMessaging}' AND payload->>'actorId' = '{Guid.Empty}'"));
    }

    [Fact]
    public async Task AnExistingPhoneIsLinkedAndKeepsItsFirstBsuid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var existing = await CreateCustomerAsync(client, city.CityId, classification.Id); // teléfono 310 935 2187 → +573109352187

        var linked = await InScopeAsync(factory, directory => directory.EnsureAsync(
            Tenant, new WhatsAppContact("CO.AAA", "+573109352187", "Otra", null), TestContext.Current.CancellationToken));
        var otherPortfolio = await InScopeAsync(factory, directory => directory.EnsureAsync(
            Tenant, new WhatsAppContact("CO.BBB", "+573109352187", "Otra", null), TestContext.Current.CancellationToken));

        Assert.Equal(new EnsuredCustomer(existing.Id, EnsureOutcome.Linked), linked);
        Assert.Equal(new EnsuredCustomer(existing.Id, EnsureOutcome.Linked), otherPortfolio);
        Assert.Equal("CO.AAA|Complete", await ScalarAsync(database.GetConnectionString(),
            $"SELECT whatsapp_user_id || '|' || completeness FROM customers.customers WHERE id = '{existing.Id}'"));
    }

    // RF9: la carrera la arbitra IX_customers_tenant_whatsapp_user_id; el perdedor relee.
    [Fact]
    public async Task ConcurrentEnsuresOfTheSameBsuidCreateOneCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var contact = new WhatsAppContact("CO.RACE", null, "Laura", null);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            InScopeAsync(factory, directory => directory.EnsureAsync(Tenant, contact, TestContext.Current.CancellationToken))));

        Assert.Single(results.Select(result => result.CustomerId).Distinct());
        Assert.Single(results, result => result.Outcome == EnsureOutcome.Created);
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(), "SELECT count(*) FROM customers.customers WHERE whatsapp_user_id = 'CO.RACE'"));
    }

    [Fact]
    public async Task ReplacingMovesTheBsuidUnlessTheNewOneBelongsToAnotherCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var laura = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.OLD", null, "Laura", null), TestContext.Current.CancellationToken));
        _ = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.TAKEN", null, "Pedro", null), TestContext.Current.CancellationToken));

        Assert.True(await InScopeAsync(factory, d => d.ReplaceWhatsAppUserIdAsync(Tenant, "CO.OLD", "CO.NEW", TestContext.Current.CancellationToken)));
        Assert.False(await InScopeAsync(factory, d => d.ReplaceWhatsAppUserIdAsync(Tenant, "CO.OLD", "CO.NEW", TestContext.Current.CancellationToken)));
        Assert.False(await InScopeAsync(factory, d => d.ReplaceWhatsAppUserIdAsync(Tenant, "CO.NEW", "CO.TAKEN", TestContext.Current.CancellationToken)));
        Assert.Equal("CO.NEW", await ScalarAsync(database.GetConnectionString(), $"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{laura.CustomerId}'"));
    }

    [Fact]
    public async Task RefsAndIdsByNameReadOnlyThisTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var laura = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.L", null, "Laura Pérez", null), TestContext.Current.CancellationToken));

        var refs = await InScopeAsync(factory, d => d.FindRefsAsync(Tenant, [laura.CustomerId, Guid.CreateVersion7()], TestContext.Current.CancellationToken));
        var ids = await InScopeAsync(factory, d => d.FindIdsByNameAsync(Tenant, "pére", 200, TestContext.Current.CancellationToken));
        var foreign = await InScopeAsync(factory, d => d.FindIdsByNameAsync(Guid.CreateVersion7(), "pére", 200, TestContext.Current.CancellationToken));

        Assert.Equal(new CustomerWhatsAppRef(laura.CustomerId, "Laura Pérez", false), Assert.Single(refs).Value);
        Assert.Equal(laura.CustomerId, Assert.Single(ids));
        Assert.Empty(foreign);
    }
}
