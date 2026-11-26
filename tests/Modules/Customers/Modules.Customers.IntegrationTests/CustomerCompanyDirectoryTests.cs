using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-11-10 §3.2–§3.4: <see cref="ICustomerCompanyDirectory"/> contra la base. Las filas entran por SQL (incompletas
/// bastan: el directorio sólo lee nombre, razón social, contacto, estado y empresa).</summary>
public sealed class CustomerCompanyDirectoryTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid OtherTenant = Guid.Parse(OtherTenantId);

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> InsertAsync(
        string connectionString, Guid tenant, string name, string? businessName = null, Guid? companyId = null,
        Guid? mergedInto = null, bool isActive = true, string? phone = null, string? email = null, string? number = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO customers.customers
                (id, tenant_id, name, business_name, is_active, with_retention, vat_surplus, version, created_at, updated_at,
                 completeness, phone, email, identification_type, identification_number, company_id, merged_into_id, merged_at)
            VALUES (@id, @tenant, @name, @business, @active, false, false, 1, now(), now(),
                    'Incomplete', @phone, @email, @type, @number, @company, @merged, CASE WHEN @merged::uuid IS NULL THEN NULL ELSE now() END)
            """,
            ("id", id), ("tenant", tenant), ("name", name), ("business", businessName), ("active", isActive),
            ("phone", phone), ("email", email), ("type", number is null ? null : "Nit"), ("number", number), ("company", companyId), ("merged", mergedInto));
        return id;
    }

    private static async Task<T> WithDirectoryAsync<T>(QepApiFactory factory, Func<ICustomerCompanyDirectory, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICustomerCompanyDirectory>());
    }

    // La fábrica arranca el host, que migra la base; la ciudad no se usa, sólo fuerza el arranque y la semilla del tenant.
    private static async Task<(QepApiFactory Factory, string ConnectionString)> StartAsync(TestDatabase database)
    {
        var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await EnsureCityAsync(client);
        return (factory, database.GetConnectionString());
    }

    [Fact]
    public async Task FindCompaniesReturnsOnlyCustomersThatHaveACompany()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connection) = await StartAsync(database);
        using var _ = factory;
        var company = await InsertAsync(connection, Tenant, "Acme", "Acme S.A.S.", number: "900.111.222-3");
        var contact = await InsertAsync(connection, Tenant, "Laura", companyId: company);
        var loose = await InsertAsync(connection, Tenant, "Pedro");

        var result = await WithDirectoryAsync(factory, directory => directory.FindCompaniesAsync(
            Tenant, [contact, loose, company], TestContext.Current.CancellationToken));

        var found = Assert.Single(result);
        Assert.Equal(contact, found.Key);
        Assert.Equal(company, found.Value.Id);
        Assert.Equal("900.111.222-3", found.Value.IdentificationNumber);
        Assert.False(found.Value.IsComplete);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task TheCompanyNameIsTheBusinessNameAndFallsBackToTheName(string? blankBusinessName)
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connection) = await StartAsync(database);
        using var _ = factory;
        var named = await InsertAsync(connection, Tenant, "Acme", "Acme S.A.S.");
        var unnamed = await InsertAsync(connection, Tenant, "Beta Ltda", blankBusinessName);
        var first = await InsertAsync(connection, Tenant, "Laura", companyId: named);
        var second = await InsertAsync(connection, Tenant, "Pedro", companyId: unnamed);

        var result = await WithDirectoryAsync(factory, directory => directory.FindCompaniesAsync(
            Tenant, [first, second], TestContext.Current.CancellationToken));

        Assert.Equal("Acme S.A.S.", result[first].Name);
        Assert.Equal("Beta Ltda", result[second].Name);
    }

    [Fact]
    public async Task ADeactivatedCompanyStillTravelsWithIsActiveFalse()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connection) = await StartAsync(database);
        using var _ = factory;
        var company = await InsertAsync(connection, Tenant, "Acme", isActive: false);
        var contact = await InsertAsync(connection, Tenant, "Laura", companyId: company);

        var result = await WithDirectoryAsync(factory, directory => directory.FindCompaniesAsync(
            Tenant, [contact], TestContext.Current.CancellationToken));

        Assert.False(result[contact].IsActive);
    }

    [Fact]
    public async Task ListContactsSkipsMergedCustomersAndOrdersByName()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connection) = await StartAsync(database);
        using var _ = factory;
        var company = await InsertAsync(connection, Tenant, "Acme");
        var zoe = await InsertAsync(connection, Tenant, "Zoe", companyId: company, phone: "3001112233", email: "zoe@acme.co");
        var ana = await InsertAsync(connection, Tenant, "Ana", companyId: company);
        var survivor = await InsertAsync(connection, Tenant, "Otro");
        await InsertAsync(connection, Tenant, "Beto", companyId: company, mergedInto: survivor);

        var result = await WithDirectoryAsync(factory, directory => directory.ListContactsAsync(
            Tenant, company, 10, TestContext.Current.CancellationToken));

        Assert.Equal([ana, zoe], result.Select(contact => contact.Id));
        Assert.Equal("3001112233", result[1].Phone);
        Assert.Equal("zoe@acme.co", result[1].Email);
    }

    [Fact]
    public async Task ListContactsHonoursTake()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connection) = await StartAsync(database);
        using var _ = factory;
        var company = await InsertAsync(connection, Tenant, "Acme");
        await InsertAsync(connection, Tenant, "Ana", companyId: company);
        await InsertAsync(connection, Tenant, "Beto", companyId: company);
        await InsertAsync(connection, Tenant, "Carla", companyId: company);

        var result = await WithDirectoryAsync(factory, directory => directory.ListContactsAsync(
            Tenant, company, 2, TestContext.Current.CancellationToken));

        Assert.Equal(["Ana", "Beto"], result.Select(contact => contact.Name));
    }

    [Fact]
    public async Task CountContactsCountsPerCompanyAndSkipsMerged()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connection) = await StartAsync(database);
        using var _ = factory;
        var acme = await InsertAsync(connection, Tenant, "Acme");
        var beta = await InsertAsync(connection, Tenant, "Beta");
        var empty = await InsertAsync(connection, Tenant, "Vacía");
        var survivor = await InsertAsync(connection, Tenant, "Otro");
        await InsertAsync(connection, Tenant, "Ana", companyId: acme);
        await InsertAsync(connection, Tenant, "Beto", companyId: acme);
        await InsertAsync(connection, Tenant, "Carla", companyId: acme, mergedInto: survivor);
        await InsertAsync(connection, Tenant, "Dani", companyId: beta);

        var result = await WithDirectoryAsync(factory, directory => directory.CountContactsAsync(
            Tenant, [acme, beta, empty], TestContext.Current.CancellationToken));

        Assert.Equal(2, result[acme]);
        Assert.Equal(1, result[beta]);
        Assert.False(result.ContainsKey(empty));
    }

    [Fact]
    public async Task FindContactOfReturnsNullForACustomerOfAnotherCompanyOrTenant()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connection) = await StartAsync(database);
        using var _ = factory;
        var acme = await InsertAsync(connection, Tenant, "Acme");
        var beta = await InsertAsync(connection, Tenant, "Beta");
        var survivor = await InsertAsync(connection, Tenant, "Otro");
        var mine = await InsertAsync(connection, Tenant, "Ana", companyId: acme);
        var merged = await InsertAsync(connection, Tenant, "Beto", companyId: acme, mergedInto: survivor);
        var foreignCompany = await InsertAsync(connection, OtherTenant, "Ajena");
        var foreign = await InsertAsync(connection, OtherTenant, "Ajeno", companyId: foreignCompany);

        var found = await WithDirectoryAsync(factory, directory => directory.FindContactOfAsync(
            Tenant, acme, mine, TestContext.Current.CancellationToken));
        var ofAnotherCompany = await WithDirectoryAsync(factory, directory => directory.FindContactOfAsync(
            Tenant, beta, mine, TestContext.Current.CancellationToken));
        var isMerged = await WithDirectoryAsync(factory, directory => directory.FindContactOfAsync(
            Tenant, acme, merged, TestContext.Current.CancellationToken));
        var ofAnotherTenant = await WithDirectoryAsync(factory, directory => directory.FindContactOfAsync(
            Tenant, foreignCompany, foreign, TestContext.Current.CancellationToken));

        Assert.Equal(mine, found?.Id);
        Assert.Null(ofAnotherCompany);
        Assert.Null(isMerged);
        Assert.Null(ofAnotherTenant);
    }

    [Fact]
    public async Task NothingCrossesTenants()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connection) = await StartAsync(database);
        using var _ = factory;
        var company = await InsertAsync(connection, OtherTenant, "Ajena");
        var contact = await InsertAsync(connection, OtherTenant, "Ajeno", companyId: company);

        var companies = await WithDirectoryAsync(factory, directory => directory.FindCompaniesAsync(
            Tenant, [contact], TestContext.Current.CancellationToken));
        var contacts = await WithDirectoryAsync(factory, directory => directory.ListContactsAsync(
            Tenant, company, 10, TestContext.Current.CancellationToken));
        var counts = await WithDirectoryAsync(factory, directory => directory.CountContactsAsync(
            Tenant, [company], TestContext.Current.CancellationToken));

        Assert.Empty(companies);
        Assert.Empty(contacts);
        Assert.Empty(counts);
    }
}
