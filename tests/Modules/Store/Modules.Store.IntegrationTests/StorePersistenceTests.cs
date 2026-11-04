using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Store.Application;
using Modules.Store.Domain;
using Modules.Store.Infrastructure.Persistence;
using Npgsql;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// El esquema <c>store</c>: el ida y vuelta de la tienda y de las solicitudes, la traducción de errores por nombre de índice, el
/// consecutivo por tenant y los CHECK que la base impone aunque el dominio ya valide.
/// </summary>
public sealed class StorePersistenceTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid OtherTenant = Guid.Parse(OtherTenantId);
    private static readonly Guid Caller = Guid.Parse(SubjectId);
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool KnowsSomeCountries(string code) => code is "CO" or "ES" or "US";

    private static StoreCurrency Currency(string code, string bank, string account) =>
        new(code, new StoreBillingAccount(Guid.NewGuid(), bank, account, code));

    private static OnlineStore ConfiguredStore(
        Guid tenant,
        string? slug = "acme",
        IReadOnlyList<StoreCurrency>? currencies = null,
        string? primaryCurrency = "COP",
        IReadOnlyList<string>? countries = null,
        IReadOnlyList<Guid>? featured = null)
    {
        var store = OnlineStore.CreateEmpty(tenant, Now);
        Configure(store, slug, currencies, primaryCurrency, countries, featured);
        return store;
    }

    private static void Configure(
        OnlineStore store,
        string? slug = "acme",
        IReadOnlyList<StoreCurrency>? currencies = null,
        string? primaryCurrency = "COP",
        IReadOnlyList<string>? countries = null,
        IReadOnlyList<Guid>? featured = null) =>
        store.Configure(
            new StoreSettingsInput(
                slug,
                Name: null,
                PrimaryColor: "#1F4FD8",
                OnPrimaryColor: null,
                AccentTextColor: null,
                BannerFileId: null,
                BannerText: null,
                Contact: new StoreContact(null, null, null, null, null, null),
                DataPolicyUrl: "https://acme.test/politica",
                PricingMode: StorePricingMode.RetailOnly,
                Currencies: currencies ?? [Currency("COP", "Bancolombia", "123-456")],
                PrimaryCurrency: primaryCurrency,
                SalesCountries: countries ?? ["CO"],
                FeaturedCategoryIds: featured ?? []),
            KnowsSomeCountries,
            Caller,
            Now);

    private static StorePlace Colombia() =>
        new("CO", Guid.NewGuid(), "Antioquia", Guid.NewGuid(), "Medellín");

    private static StoreRequestBilling Billing(StorePlace? place = null) =>
        new("Ana Pérez", "CC", "123456", "300 123 4567", "+573001234567", "ana@test.co", "Calle 1 # 2-3", place ?? Colombia());

    private static StoreRequestShipping Shipping() =>
        new("Luis Gómez", "300 765 4321", "+573007654321", "Carrera 9 # 8-7", Colombia());

    private static StoreOrderRequestLineInput Line(string name, decimal quantity) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, quantity, 10_000m, 5m, "Scale", 9_500m, 9_500m * quantity);

    private static StoreOrderRequest NewRequest(
        Guid tenant,
        long number,
        Guid? clientRequestId = null,
        StoreRequestBilling? billing = null,
        StoreRequestShipping? shipping = null,
        DateTimeOffset? receivedAt = null,
        IReadOnlyList<StoreOrderRequestLineInput>? lines = null) =>
        StoreOrderRequest.Create(
            Guid.NewGuid(),
            tenant,
            number,
            clientRequestId ?? Guid.NewGuid(),
            billing ?? Billing(),
            shipping,
            note: "Entregar en portería",
            currency: "COP",
            mode: StorePricingMode.RetailOnly,
            lines: lines ?? [Line("Camiseta", 2m)],
            subtotal: 20_000m,
            discountAmount: 1_000m,
            taxAmount: 3_610m,
            total: 22_610m,
            consent: new StoreConsent(Now, "203.0.113.5", "https://acme.test/politica", Now.AddDays(-3)),
            receivedAt: receivedAt ?? Now);

    private static async Task SaveStoreAsync(QepApiFactory factory, OnlineStore store)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IOnlineStoreRepository>().Add(store);
        await scope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task SaveRequestAsync(QepApiFactory factory, StoreOrderRequest request)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IStoreOrderRequestRepository>().Add(request);
        await scope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task<OnlineStore> ReloadStoreAsync(QepApiFactory factory, Guid tenant)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var found = await scope.ServiceProvider.GetRequiredService<IOnlineStoreRepository>().FindAsync(tenant, Ct);
        return found ?? throw new InvalidOperationException("The store was not found.");
    }

    private static async Task<StoreOrderRequest> ReloadRequestAsync(QepApiFactory factory, Guid tenant, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var found = await scope.ServiceProvider.GetRequiredService<IStoreOrderRequestRepository>().FindAsync(tenant, id, Ct);
        return found ?? throw new InvalidOperationException("The request was not found.");
    }

    private static async Task<long> ScalarAsync(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        return await db.Database.SqlQuery<long>(sql).SingleAsync(Ct);
    }

    private static async Task ExecuteAsync(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(sql, Ct);
    }

    /// <summary>Una fila mínima y válida de <c>store.stores</c>, para colgarle monedas por SQL crudo.</summary>
    private static Task InsertBareStoreAsync(QepApiFactory factory, Guid tenant) =>
        ExecuteAsync(
            factory,
            $"""
            INSERT INTO store.stores (tenant_id, is_published, pricing_mode, version)
            VALUES ({tenant}, false, 'RetailOnly', 0)
            """);

    /// <summary>Todas las columnas obligatorias de una solicitud válida; cada prueba pisa las que quiere romper.</summary>
    private static Dictionary<string, object?> ValidRequestRow(Guid tenant, long number) =>
        new()
        {
            ["id"] = Guid.NewGuid(),
            ["tenant_id"] = tenant,
            ["number"] = number,
            ["client_request_id"] = Guid.NewGuid(),
            ["status"] = "New",
            ["billing_name"] = "Ana Pérez",
            ["billing_identification_type"] = "CC",
            ["billing_identification_number"] = "123456",
            ["billing_phone"] = "300 123 4567",
            ["billing_phone_e164"] = "+573001234567",
            ["billing_email"] = "ana@test.co",
            ["billing_address"] = "Calle 1 # 2-3",
            ["billing_country"] = "CO",
            ["billing_department_id"] = Guid.NewGuid(),
            ["billing_department_name"] = "Antioquia",
            ["billing_city_id"] = Guid.NewGuid(),
            ["billing_city_name"] = "Medellín",
            ["has_shipping"] = false,
            ["currency"] = "COP",
            ["pricing_mode"] = "RetailOnly",
            ["subtotal"] = 100m,
            ["discount_amount"] = 0m,
            ["tax_amount"] = 0m,
            ["total"] = 100m,
            ["consent_at"] = Now,
            ["consent_ip"] = "203.0.113.5",
            ["consent_policy_url"] = "https://acme.test/politica",
            ["consent_policy_set_at"] = Now,
            ["received_at"] = Now,
            ["version"] = 1L,
        };

    private static async Task<PostgresException> InsertRequestRowAsync(
        QepApiFactory factory, Dictionary<string, object?> row)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var connection = (NpgsqlConnection)scope.ServiceProvider.GetRequiredService<StoreDbContext>().Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand();
        insert.Connection = connection;
        var columns = row.Keys.ToList();
        insert.CommandText = $"INSERT INTO store.order_requests ({string.Join(", ", columns)}) "
            + $"VALUES ({string.Join(", ", columns.Select(column => "@" + column))})";
        foreach (var column in columns)
        {
            insert.Parameters.AddWithValue(column, row[column] ?? DBNull.Value);
        }

        return await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task AStoreWithFeaturedCategoriesRoundTrips()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var featured = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await SaveStoreAsync(factory, ConfiguredStore(Tenant, featured: featured));

        var found = await ReloadStoreAsync(factory, Tenant);

        Assert.Equal(featured, found.FeaturedCategoryIds);
        Assert.Equal("acme", found.Slug);
        Assert.Equal(1, found.Version);
    }

    [Fact]
    public async Task AStoreWithTwoCurrenciesRoundTripsInOrder()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var eur = Currency("EUR", "Banco B", "B-222");
        var cop = Currency("COP", "Banco A", "A-111");
        await SaveStoreAsync(factory, ConfiguredStore(Tenant, currencies: [eur, cop], primaryCurrency: "COP"));

        var found = await ReloadStoreAsync(factory, Tenant);

        // EUR primero aunque COP ordene antes por clave: el orden es el que eligió quien configuró la tienda.
        Assert.Equal(["EUR", "COP"], found.Currencies.Select(value => value.Currency));
        Assert.Equal("COP", found.PrimaryCurrency);
        Assert.Equal(eur.BillingAccount, found.BillingAccountFor("EUR"));
        Assert.Equal(cop.BillingAccount, found.BillingAccountFor("COP"));
    }

    [Fact]
    public async Task ReplacingTheCurrenciesLeavesOnlyTheNewList()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await SaveStoreAsync(
            factory,
            ConfiguredStore(
                Tenant,
                currencies: [Currency("COP", "Banco A", "A-111"), Currency("EUR", "Banco B", "B-222")]));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = await scope.ServiceProvider.GetRequiredService<IOnlineStoreRepository>().FindAsync(Tenant, Ct);
            Configure(store!, currencies: [Currency("EUR", "Banco C", "C-333")], primaryCurrency: "EUR");
            await scope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct);
        }

        var found = await ReloadStoreAsync(factory, Tenant);

        Assert.Equal(1, await ScalarAsync(factory, $"SELECT count(*) AS \"Value\" FROM store.store_currencies WHERE tenant_id = {Tenant}"));
        var only = Assert.Single(found.Currencies);
        Assert.Equal("EUR", only.Currency);
        Assert.Equal("C-333", only.BillingAccount.AccountNumber);
        Assert.Equal("EUR", found.PrimaryCurrency);
    }

    [Fact]
    public async Task TheDatabaseRejectsAnAccountInAnotherCurrency()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await InsertBareStoreAsync(factory, Tenant);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO store.store_currencies
                (tenant_id, currency, position, billing_company_id, billing_bank_name, billing_account_number, billing_currency)
            VALUES ({Tenant}, 'EUR', 0, {Guid.NewGuid()}, 'Banco', '123', 'COP')
            """,
            Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_store_currencies_account_currency", exception.ConstraintName);
    }

    [Fact]
    public async Task TwoTenantsCannotShareASlug()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await SaveStoreAsync(factory, ConfiguredStore(Tenant, slug: "acme"));

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IOnlineStoreRepository>().Add(ConfiguredStore(OtherTenant, slug: "acme"));
        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => scope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal(StoreErrorCodes.SlugTaken, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        Assert.True(await read.ServiceProvider.GetRequiredService<IOnlineStoreRepository>()
            .SlugTakenByAnotherAsync(OtherTenant, "acme", Ct));
    }

    [Fact]
    public async Task ReplacingTheSubdomainLeavesOneRowPerTenant()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStoreDomainRepository>()
                .ReplaceSubdomainAsync(Tenant, "acme", "lulocrm.com", Ct);
            await scope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStoreDomainRepository>()
                .ReplaceSubdomainAsync(Tenant, "acme-nuevo", "lulocrm.com", Ct);
            await scope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var domains = read.ServiceProvider.GetRequiredService<IStoreDomainRepository>();
        var current = await domains.FindByHostAsync("acme-nuevo.lulocrm.com", Ct);
        Assert.NotNull(current);
        Assert.Equal(Tenant, current.TenantId);
        Assert.Equal(StoreDomainKind.Subdomain, current.Kind);
        Assert.Null(await domains.FindByHostAsync("acme.lulocrm.com", Ct));
        Assert.Equal(1, await ScalarAsync(factory, $"SELECT count(*) AS \"Value\" FROM store.store_domains WHERE tenant_id = {Tenant}"));
    }

    [Fact]
    public async Task ARequestRoundTripsWithItsLinesInOrder()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        // Los nombres no ordenan igual que las posiciones: lo que se lee tiene que seguir el orden en que se guardó.
        var request = NewRequest(
            Tenant,
            number: 1,
            lines: [Line("Zapato", 1m), Line("Abrigo", 3m), Line("Medias", 12m)]);
        await SaveRequestAsync(factory, request);

        var found = await ReloadRequestAsync(factory, Tenant, request.Id);

        Assert.Equal(["Zapato", "Abrigo", "Medias"], found.Lines.Select(value => value.Name));
        Assert.Equal([0, 1, 2], found.Lines.Select(value => value.Position));
        Assert.Equal(request.Lines.Select(value => value.VariantId), found.Lines.Select(value => value.VariantId));
        Assert.Equal(12m, found.Lines[2].Quantity);
        Assert.Equal(9_500m, found.Lines[0].DiscountedUnitPrice);
        Assert.Equal("Scale", found.Lines[0].DiscountOrigin);
        Assert.Equal(StoreOrderRequestStatus.New, found.Status);
        Assert.Equal(1, found.Number);
        Assert.Equal("T-0001", found.DisplayNumber);
        Assert.Equal(22_610m, found.Total);
        Assert.Equal(request.Consent, found.Consent);
        Assert.Equal("Entregar en portería", found.Note);
        Assert.Equal(1, found.Version);
    }

    [Fact]
    public async Task ARequestRoundTripsItsBillingAndShipping()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var withShipping = NewRequest(Tenant, number: 1, shipping: Shipping());
        var withoutShipping = NewRequest(Tenant, number: 2);
        await SaveRequestAsync(factory, withShipping);
        await SaveRequestAsync(factory, withoutShipping);

        var first = await ReloadRequestAsync(factory, Tenant, withShipping.Id);
        var second = await ReloadRequestAsync(factory, Tenant, withoutShipping.Id);

        Assert.Equal(withShipping.Billing, first.Billing);
        Assert.Equal(withShipping.Shipping, first.Shipping);
        Assert.Equal(withoutShipping.Billing, second.Billing);
        Assert.Null(second.Shipping);
        Assert.Equal(1, await ScalarAsync(
            factory,
            $"SELECT count(*) AS \"Value\" FROM store.order_requests WHERE id = {withShipping.Id} AND has_shipping"));
        Assert.Equal(1, await ScalarAsync(
            factory,
            $"SELECT count(*) AS \"Value\" FROM store.order_requests WHERE id = {withoutShipping.Id} AND NOT has_shipping AND shipping_phone IS NULL"));
    }

    [Fact]
    public async Task TheDatabaseRejectsAHalfShipping()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var row = ValidRequestRow(Tenant, 1);
        row["has_shipping"] = true;
        row["shipping_phone"] = "300 765 4321";
        row["shipping_phone_e164"] = "+573007654321";
        row["shipping_country"] = "CO";
        row["shipping_department_id"] = Guid.NewGuid();
        row["shipping_department_name"] = "Antioquia";
        row["shipping_city_id"] = Guid.NewGuid();
        row["shipping_city_name"] = "Medellín";
        row["shipping_address"] = null;

        var exception = await InsertRequestRowAsync(factory, row);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_order_requests_shipping", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabaseRejectsAColombianBillingWithoutCityId()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var row = ValidRequestRow(Tenant, 1);
        row["billing_city_id"] = null;

        var exception = await InsertRequestRowAsync(factory, row);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_order_requests_billing_lane", exception.ConstraintName);
    }

    [Fact]
    public async Task AForeignBillingRoundTripsWithItsCityText()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var foreign = new StorePlace("ES", null, null, null, "Madrid");
        var request = NewRequest(Tenant, number: 1, billing: Billing(foreign));
        await SaveRequestAsync(factory, request);

        var found = await ReloadRequestAsync(factory, Tenant, request.Id);

        Assert.Equal("ES", found.Billing.Place.Country);
        Assert.Equal("Madrid", found.Billing.Place.CityName);
        Assert.Null(found.Billing.Place.CityId);
        Assert.Null(found.Billing.Place.DepartmentId);
        Assert.Null(found.Billing.Place.DepartmentName);
        Assert.False(found.Billing.Place.IsColombian);
    }

    [Fact]
    public async Task SalesCountriesRoundTripInOrder()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await SaveStoreAsync(factory, ConfiguredStore(Tenant, countries: ["US", "CO", "ES"]));

        var found = await ReloadStoreAsync(factory, Tenant);

        Assert.Equal(["US", "CO", "ES"], found.SalesCountries);
    }

    [Fact]
    public async Task TheSameClientRequestIdCannotBeStoredTwice()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var clientRequestId = Guid.NewGuid();
        await SaveRequestAsync(factory, NewRequest(Tenant, number: 1, clientRequestId: clientRequestId));

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => SaveRequestAsync(factory, NewRequest(Tenant, number: 2, clientRequestId: clientRequestId)));

        Assert.Equal(StoreErrorCodes.DuplicateClientRequest, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        var existing = await read.ServiceProvider.GetRequiredService<IStoreOrderRequestRepository>()
            .FindByClientRequestIdAsync(Tenant, clientRequestId, Ct);
        Assert.Equal(1, existing!.Number);
    }

    [Fact]
    public async Task AConcurrentRequestEditIsAConflictNot412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var request = NewRequest(Tenant, number: 1);
        await SaveRequestAsync(factory, request);

        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var first = await firstScope.ServiceProvider.GetRequiredService<IStoreOrderRequestRepository>()
            .FindAsync(Tenant, request.Id, Ct);
        var second = await secondScope.ServiceProvider.GetRequiredService<IStoreOrderRequestRepository>()
            .FindAsync(Tenant, request.Id, Ct);
        first!.BeginConversion(Caller, Now, TimeSpan.FromMinutes(2));
        second!.BeginConversion(Guid.NewGuid(), Now, TimeSpan.FromMinutes(2));

        await firstScope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct);
        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => secondScope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal(StoreErrorCodes.OrderRequestConflict, exception.Code);
    }

    [Fact]
    public async Task RequestNumbersAreConsecutivePerTenantAndStartAtOne()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await using var scope = factory.Services.CreateAsyncScope();
        var numbers = scope.ServiceProvider.GetRequiredService<IStoreRequestNumberGenerator>();

        Assert.Equal(1, await numbers.NextAsync(Tenant, Ct));
        Assert.Equal(2, await numbers.NextAsync(Tenant, Ct));
        Assert.Equal(1, await numbers.NextAsync(OtherTenant, Ct));
        Assert.Equal(3, await numbers.NextAsync(Tenant, Ct));
        Assert.Equal(2, await numbers.NextAsync(OtherTenant, Ct));
    }

    [Fact]
    public async Task CountsSeparateUntakenMineAndToday()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var todayStart = new DateTimeOffset(2026, 11, 3, 5, 0, 0, TimeSpan.Zero);
        var todayEnd = todayStart.AddDays(1);
        var otherMember = Guid.NewGuid();

        // Tres sin tomar (dos de hoy, una de anteayer), una convertida por mí, una por otro asesor y una descartada.
        await SaveRequestAsync(factory, NewRequest(Tenant, 1, receivedAt: todayStart.AddHours(2)));
        await SaveRequestAsync(factory, NewRequest(Tenant, 2, receivedAt: todayStart.AddHours(9)));
        await SaveRequestAsync(factory, NewRequest(Tenant, 3, receivedAt: todayStart.AddDays(-2)));
        var mine = NewRequest(Tenant, 4, receivedAt: todayStart.AddHours(3));
        mine.MarkConverted(Guid.NewGuid(), Guid.NewGuid(), "P-1", 22_610m, Caller, Now);
        await SaveRequestAsync(factory, mine);
        var theirs = NewRequest(Tenant, 5, receivedAt: todayStart.AddDays(-3));
        theirs.MarkConverted(Guid.NewGuid(), Guid.NewGuid(), "P-2", 22_610m, otherMember, Now);
        await SaveRequestAsync(factory, theirs);
        var discarded = NewRequest(Tenant, 6, receivedAt: todayStart.AddDays(-4));
        discarded.Discard(Caller, StoreDiscardReason.Spam, null, Now);
        await SaveRequestAsync(factory, discarded);
        // De otro tenant: no entra en ninguna cuenta.
        await SaveRequestAsync(factory, NewRequest(OtherTenant, 1, receivedAt: todayStart.AddHours(4)));

        await using var scope = factory.Services.CreateAsyncScope();
        var requests = scope.ServiceProvider.GetRequiredService<IStoreOrderRequestRepository>();
        var counts = await requests.CountAsync(Tenant, Caller, todayStart, todayEnd, Ct);
        var (untaken, untakenTotal) = await requests.ListAsync(
            new StoreInboxFilter(Tenant, StoreInboxView.Untaken, Caller, 1, 10), Ct);
        var (takenByMe, takenTotal) = await requests.ListAsync(
            new StoreInboxFilter(Tenant, StoreInboxView.TakenByMe, Caller, 1, 10), Ct);
        var (all, allTotal) = await requests.ListAsync(
            new StoreInboxFilter(Tenant, StoreInboxView.All, Caller, 1, 10), Ct);

        Assert.Equal(new StoreInboxCounts(Untaken: 3, TakenByMe: 1, ReceivedToday: 3), counts);
        Assert.Equal(3, untakenTotal);
        // Del más reciente al más viejo.
        Assert.Equal([2L, 1L, 3L], untaken.Select(value => value.Number));
        Assert.Equal([4L], takenByMe.Select(value => value.Number));
        Assert.Equal(1, takenTotal);
        Assert.Equal(6, allTotal);
        Assert.Equal(6, all.Count);
        Assert.Equal(3, await requests.CountReceivedSinceAsync(Tenant, todayStart, Ct));
    }

    [Fact]
    public async Task TheDatabaseRejectsAnUnknownStatus()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var row = ValidRequestRow(Tenant, 1);
        row["status"] = "Bogus";

        var exception = await InsertRequestRowAsync(factory, row);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_order_requests_status", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabaseRejectsAnUnknownPricingMode()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            factory,
            $"""
            INSERT INTO store.stores (tenant_id, is_published, pricing_mode, version)
            VALUES ({Tenant}, false, 'Bogus', 0)
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_stores_pricing_mode", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabaseRejectsALowercaseColor()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            factory,
            $"""
            INSERT INTO store.stores (tenant_id, is_published, pricing_mode, primary_color, version)
            VALUES ({Tenant}, false, 'RetailOnly', '#1f4fd8', 0)
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_stores_colors", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabaseRejectsAnUnknownDiscardReason()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var row = ValidRequestRow(Tenant, 1);
        row["discard_reason"] = "Bogus";

        var exception = await InsertRequestRowAsync(factory, row);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_order_requests_discard_reason", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabaseRejectsAColombianShippingWithoutCityId()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        // Completo para CK_order_requests_shipping; sólo falta el id de la ciudad que pide el carril colombiano.
        var row = ValidRequestRow(Tenant, 1);
        row["has_shipping"] = true;
        row["shipping_phone"] = "300 765 4321";
        row["shipping_phone_e164"] = "+573007654321";
        row["shipping_address"] = "Carrera 4 # 5-6";
        row["shipping_country"] = "CO";
        row["shipping_department_id"] = Guid.NewGuid();
        row["shipping_department_name"] = "Antioquia";
        row["shipping_city_id"] = null;
        row["shipping_city_name"] = "Medellín";

        var exception = await InsertRequestRowAsync(factory, row);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_order_requests_shipping_lane", exception.ConstraintName);
    }

    [Fact]
    public async Task TheBillingPhoneIndexExists()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        // Lo crea a mano la migración InitialStore (EF no lo expresa): si alguien la regenera sin él, esto lo atrapa.
        var count = await ScalarAsync(
            factory,
            $"SELECT count(*) AS \"Value\" FROM pg_indexes WHERE schemaname = 'store' AND indexname = 'IX_order_requests_tenant_billing_phone'");

        Assert.Equal(1, count);
    }
}
