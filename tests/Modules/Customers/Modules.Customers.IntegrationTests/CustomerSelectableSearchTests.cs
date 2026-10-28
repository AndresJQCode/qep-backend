using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Desviación B1 del plan POS contra la base: los clientes que la caja puede elegir (activos y con documento) se
/// filtran en la consulta y no después, porque los incompletos de WhatsApp —sin documento— son los más nuevos y llenarían
/// la página antes de llegar al cliente que se busca.</summary>
public sealed class CustomerSelectableSearchTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid OtherTenant = Guid.Parse(OtherTenantId);
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // Se siembra por dominio y no por la API: el tiempo (CreatedAt) tiene que ser el que la prueba dice, y crear 60
    // incompletos uno a uno por HTTP no aporta nada a una prueba que trata de la consulta.
    private static async Task AddAsync(QepApiFactory factory, Func<IPhoneNumberNormalizer, IEnumerable<Customer>> build)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
        var normalizer = scope.ServiceProvider.GetRequiredService<IPhoneNumberNormalizer>();
        foreach (var customer in build(normalizer))
        {
            repository.Add(customer);
        }

        await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Customer WithDocument(
        IPhoneNumberNormalizer normalizer, Guid tenant, string name, string number, DateTimeOffset createdAt) =>
        Customer.CreateFromRegister(
            new CustomerId(Guid.CreateVersion7()),
            tenant,
            name,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = number },
            null,
            createdAt,
            normalizer);

    private static Customer WithoutDocument(
        IPhoneNumberNormalizer normalizer, string name, int n, DateTimeOffset createdAt) =>
        Customer.CreateIncomplete(
            new CustomerId(Guid.CreateVersion7()),
            Tenant,
            name,
            null,
            "CO",
            $"57300{n:D7}",
            createdAt,
            normalizer);

    private static async Task<IReadOnlyList<Customer>> SearchAsync(QepApiFactory factory, string? search, int take)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICustomerRepository>()
            .SearchSelectableAsync(Tenant, search, take, TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SelectableSearchSkipsTheNewestWithoutDocument()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        await AddAsync(factory, normalizer =>
            [
                WithDocument(normalizer, Tenant, "Cliente Viejo", "1020304050", Base),
                .. Enumerable.Range(1, 60).Select(n => WithoutDocument(normalizer, $"Whats {n}", n, Base.AddHours(n))),
            ]);

        var found = await SearchAsync(factory, null, 20);

        Assert.Equal("Cliente Viejo", Assert.Single(found).Name);
    }

    [Fact]
    public async Task SelectableSearchExcludesInactiveCustomers()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        await AddAsync(factory, normalizer =>
            [
                WithDocument(normalizer, Tenant, "Laura Inactiva", "1020304050", Base),
                WithDocument(normalizer, Tenant, "Laura Activa", "1020304051", Base),
            ]);
        await ExecuteAsync(
            database.GetConnectionString(),
            $"UPDATE customers.customers SET is_active = false WHERE tenant_id = '{Tenant}' AND name = 'Laura Inactiva'");

        var found = await SearchAsync(factory, "Laura", 20);

        Assert.Equal("Laura Activa", Assert.Single(found).Name);
    }

    [Fact]
    public async Task SelectableSearchMatchesNameDocumentOrCucAndKeepsTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var complete = await CreateCustomerAsync(client, city.CityId, classification.Id, "Verde Esencial S.A.S.", "900.123.456-1");
        await AddAsync(factory, normalizer =>
            [
                WithDocument(normalizer, Tenant, "Laura Pérez", "1020304050", Base),
                WithDocument(normalizer, OtherTenant, "Laura Ajena", "1020304050", Base),
            ]);

        var byDocument = await SearchAsync(factory, "1020304050", 20);
        var byCuc = await SearchAsync(factory, complete.Cuc![3..], 20);
        var byName = await SearchAsync(factory, "esencial", 20);

        Assert.Equal("Laura Pérez", Assert.Single(byDocument).Name);
        Assert.Equal(complete.Id, Assert.Single(byCuc).Id.Value);
        Assert.Equal(complete.Id, Assert.Single(byName).Id.Value);
    }

    [Fact]
    public async Task SelectableSearchIncludesAnIncompleteWithDocument()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        await AddAsync(factory, normalizer => [WithDocument(normalizer, Tenant, "Mostrador Uno", "55667788", Base)]);

        var found = Assert.Single(await SearchAsync(factory, "55667788", 20));

        Assert.Equal("Mostrador Uno", found.Name);
        Assert.False(found.IsComplete);
    }

    [Fact]
    public async Task SelectableSearchWithoutTextReturnsTheNewestFirstUpToTake()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        await AddAsync(factory, normalizer =>
            Enumerable.Range(1, 25).Select(n =>
                WithDocument(normalizer, Tenant, $"Cliente {n:D2}", $"7000{n:D4}", Base.AddMinutes(n))));

        var found = await SearchAsync(factory, null, 20);

        Assert.Equal(20, found.Count);
        Assert.Equal("Cliente 25", found[0].Name);
        Assert.Equal("Cliente 06", found[^1].Name);
    }
}
