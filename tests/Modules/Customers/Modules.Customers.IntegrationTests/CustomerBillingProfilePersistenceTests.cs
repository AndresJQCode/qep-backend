using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// La libreta de perfiles de facturación en la base (spec 2026-11-24 §4): <c>customers.customer_billing_profiles</c> y sus
/// tasas en <c>customers.customer_billing_profile_tax_rates</c>, sin FK a <c>catalog</c>. Cada prueba escribe con ids
/// propios y sólo mira sus filas, así que comparten el host de la clase.
/// </summary>
public sealed class CustomerBillingProfilePersistenceTests(SharedCustomersHost sharedHost)
    : IClassFixture<SharedCustomersHost>
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RateA = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid RateB = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    private string ConnectionString => sharedHost.Database.GetConnectionString();

    private static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..12];

    // De afuera y sin libreta: no necesita una ciudad de DIVIPOLA, y el documento y el CUC son únicos por corrida.
    private static Customer NewCustomer()
    {
        var suffix = UniqueSuffix();
        return Customer.Create(
            CustomerId.New(),
            Tenant,
            "CLI" + suffix,
            "Casa Verde SL",
            null,
            null,
            new CustomerIdentification { Type = IdentificationType.Pasaporte, Number = "X" + suffix },
            null,
            new CustomerContactInfo
            {
                Phone = "+34 600 111 222",
                Country = "ES",
                CityName = "Madrid"
            },
            new CustomerCommercialInfo { ClassificationId = null },
            Now);
    }

    private async Task<Guid> ScalarGuidAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task SaveNewAsync(IServiceProvider services, Customer customer)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICustomerRepository>().Add(customer);
        await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // Carga con el repositorio, muta y guarda en un contexto nuevo: la misma forma que un handler.
    private static async Task ChangeAsync(IServiceProvider services, CustomerId id, Action<Customer> change)
    {
        await using var scope = services.CreateAsyncScope();
        var customer = await scope.ServiceProvider.GetRequiredService<ICustomerRepository>()
            .FindAsync(Tenant, id, TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        change(customer);
        await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Customer> LoadAsync(IServiceProvider services, CustomerId id)
    {
        await using var scope = services.CreateAsyncScope();
        var customer = await scope.ServiceProvider.GetRequiredService<ICustomerRepository>()
            .FindAsync(Tenant, id, TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return customer;
    }

    private static CustomerBillingProfileDetails Colombian(Guid cityId) =>
        new()
        {
            Name = "Comercial Andina SAS",
            PersonType = "Juridica",
            IdentificationType = "NIT",
            IdentificationNumber = "900.123.456-1",
            Phone = "3001234567",
            Email = "facturas@andina.co",
            Address = "Cra 43A # 1-50",
            Country = "CO",
            CityId = cityId,
            WithholdingTaxRateIds = [RateA, RateB],
            VatSurplus = true
        };

    private static CustomerBillingProfileDetails Spanish(Guid regionId) =>
        new()
        {
            Name = "Andina Iberia SL",
            IdentificationType = "NIF",
            IdentificationNumber = "B12345678",
            Address = "Calle Mayor 1",
            Country = "ES",
            CityName = "Madrid",
            RegionId = regionId,
            WithholdingTaxRateIds = [RateA]
        };

    [Fact]
    public async Task AProfileRoundTripsWithItsTaxRates()
    {
        var factory = sharedHost.Factory;
        var cityId = await ScalarGuidAsync("SELECT id FROM geography.cities ORDER BY id LIMIT 1");
        var regionId = await ScalarGuidAsync("SELECT id FROM geography.regions WHERE country_code = 'ES' AND code = '28'");
        var customer = NewCustomer();
        await SaveNewAsync(factory.Services, customer);

        CustomerBillingProfileId colombianId = default;
        CustomerBillingProfileId spanishId = default;
        await ChangeAsync(factory.Services, customer.Id, loaded =>
        {
            colombianId = loaded.EnsureBillingProfile(Colombian(cityId), false, Now).Profile.Id;
            spanishId = loaded.EnsureBillingProfile(Spanish(regionId), false, Now.AddMinutes(1)).Profile.Id;
        });

        var read = await LoadAsync(factory.Services, customer.Id);

        Assert.Equal(2, read.BillingProfiles.Count);
        Assert.Equal(customer.Version + 2, read.Version);
        var colombian = Assert.Single(read.BillingProfiles, profile => profile.Id == colombianId);
        Assert.Equal("Comercial Andina SAS", colombian.Name);
        Assert.Equal("Juridica", colombian.PersonType);
        Assert.Equal("NIT", colombian.IdentificationType);
        Assert.Equal("900.123.456-1", colombian.IdentificationNumber);
        Assert.Equal("3001234567", colombian.Phone);
        Assert.Equal("facturas@andina.co", colombian.Email);
        Assert.Equal("Cra 43A # 1-50", colombian.Address);
        Assert.Equal("CO", colombian.Country);
        Assert.Equal(cityId, colombian.CityId);
        Assert.Null(colombian.CityName);
        Assert.Null(colombian.RegionId);
        Assert.Equivalent(new[] { RateA, RateB }, colombian.WithholdingTaxRateIds);
        Assert.True(colombian.VatSurplus);
        Assert.True(colombian.IsPrincipal);
        Assert.Equal(Now, colombian.CreatedAt);
        Assert.Equal(Now, colombian.UpdatedAt);
        var spanish = Assert.Single(read.BillingProfiles, profile => profile.Id == spanishId);
        Assert.Null(spanish.PersonType);
        Assert.Null(spanish.Phone);
        Assert.Null(spanish.Email);
        Assert.Null(spanish.CityId);
        Assert.Equal("Madrid", spanish.CityName);
        Assert.Equal(regionId, spanish.RegionId);
        Assert.Equal([RateA], spanish.WithholdingTaxRateIds);
        Assert.False(spanish.VatSurplus);
        Assert.False(spanish.IsPrincipal);

        // Reemplazar las tasas de un perfil guardado y mover la principal: filas nuevas y viejas en el mismo guardado.
        await ChangeAsync(factory.Services, customer.Id, loaded =>
        {
            loaded.UpdateBillingProfile(
                colombianId, Colombian(cityId) with { WithholdingTaxRateIds = [RateB] }, false, Now.AddMinutes(2));
            loaded.UpdateBillingProfile(
                spanishId, Spanish(regionId) with { WithholdingTaxRateIds = [RateA, RateB] }, true, Now.AddMinutes(2));
        });

        var updated = await LoadAsync(factory.Services, customer.Id);

        Assert.Equal([RateB], updated.BillingProfiles.Single(profile => profile.Id == colombianId).WithholdingTaxRateIds);
        Assert.Equivalent(
            new[] { RateA, RateB },
            updated.BillingProfiles.Single(profile => profile.Id == spanishId).WithholdingTaxRateIds);
        Assert.Equal(spanishId, Assert.Single(updated.BillingProfiles, profile => profile.IsPrincipal).Id);
        Assert.Equal(
            2,
            await CountAsync(
                $"SELECT count(*) FROM customers.customer_billing_profile_tax_rates WHERE billing_profile_id = '{spanishId.Value}' "
                + $"AND tenant_id = '{Tenant}'"));
    }

    [Fact]
    public async Task RemovingAProfileDeletesItsTaxRates()
    {
        var factory = sharedHost.Factory;
        var cityId = await ScalarGuidAsync("SELECT id FROM geography.cities ORDER BY id LIMIT 1");
        var regionId = await ScalarGuidAsync("SELECT id FROM geography.regions WHERE country_code = 'ES' AND code = '28'");
        var customer = NewCustomer();
        await SaveNewAsync(factory.Services, customer);
        CustomerBillingProfileId removedId = default;
        await ChangeAsync(factory.Services, customer.Id, loaded =>
        {
            loaded.EnsureBillingProfile(Spanish(regionId), false, Now);
            removedId = loaded.EnsureBillingProfile(Colombian(cityId), false, Now).Profile.Id;
        });
        var ratesOfRemoved =
            $"SELECT count(*) FROM customers.customer_billing_profile_tax_rates WHERE billing_profile_id = '{removedId.Value}'";
        Assert.Equal(2, await CountAsync(ratesOfRemoved));

        await ChangeAsync(factory.Services, customer.Id, loaded => loaded.RemoveBillingProfile(removedId, Now.AddMinutes(1)));

        Assert.Equal(0, await CountAsync(ratesOfRemoved));
        Assert.Equal(
            1,
            await CountAsync(
                $"SELECT count(*) FROM customers.customer_billing_profiles WHERE customer_id = '{customer.Id.Value}'"));
    }

    // El carril de la ciudad lo respalda la base, aunque alguien se salte el dominio.
    [Fact]
    public async Task TheDatabaseRejectsAMixedCityLane()
    {
        var factory = sharedHost.Factory;
        var cityId = await ScalarGuidAsync("SELECT id FROM geography.cities ORDER BY id LIMIT 1");
        var customer = NewCustomer();
        await SaveNewAsync(factory.Services, customer);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO customers.customer_billing_profiles (
                id, customer_id, name, identification_type, identification_number, address, country, city_id, city_name,
                is_principal, created_at, updated_at)
            VALUES (@id, @customer, 'Mezclado', 'NIT', '900', 'Calle 1', 'CO', @city, 'Medellín', false, now(), now())
            """,
            connection);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("customer", customer.Id.Value);
        insert.Parameters.AddWithValue("city", cityId);

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal("CK_customer_billing_profiles_city_lane", failure.ConstraintName);
    }
}
