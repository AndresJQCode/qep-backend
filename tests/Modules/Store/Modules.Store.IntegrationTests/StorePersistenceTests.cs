using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Store.Application;
using Modules.Store.Domain;
using Modules.Store.Infrastructure.Persistence;
using Npgsql;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// El esquema <c>store</c>: el ida y vuelta de la tienda y del consentimiento de cada pedido, la traducción de errores por nombre
/// de índice, los CHECK que la base impone aunque el dominio ya valide y que las tablas de la bandeja ya no están.
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
        IReadOnlyList<Guid>? featured = null,
        bool showPrices = true)
    {
        var store = OnlineStore.CreateEmpty(tenant, Now);
        Configure(store, slug, currencies, primaryCurrency, countries, featured, showPrices);
        return store;
    }

    private static void Configure(
        OnlineStore store,
        string? slug = "acme",
        IReadOnlyList<StoreCurrency>? currencies = null,
        string? primaryCurrency = "COP",
        IReadOnlyList<string>? countries = null,
        IReadOnlyList<Guid>? featured = null,
        bool showPrices = true) =>
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
                FeaturedCategoryIds: featured ?? [],
                AdvisorMembershipId: null,
                ShowPrices: showPrices),
            KnowsSomeCountries,
            Caller,
            Now);

    private static async Task SaveStoreAsync(QepApiFactory factory, OnlineStore store)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IOnlineStoreRepository>().Add(store);
        await scope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task<OnlineStore> ReloadStoreAsync(QepApiFactory factory, Guid tenant)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var found = await scope.ServiceProvider.GetRequiredService<IOnlineStoreRepository>().FindAsync(tenant, Ct);
        return found ?? throw new InvalidOperationException("The store was not found.");
    }

    private static async Task<long> ScalarAsync(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        return await db.Database.SqlQuery<long>(sql).SingleAsync(Ct);
    }

    private static async Task<string> ScalarTextAsync(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        return await db.Database.SqlQuery<string>(sql).SingleAsync(Ct);
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

    [Fact]
    public async Task TheShowPricesColumnIsNotNullAndDefaultsToTrue()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var column = await ScalarTextAsync(
            factory,
            $"""
            SELECT is_nullable || '|' || column_default AS "Value" FROM information_schema.columns
            WHERE table_schema = 'store' AND table_name = 'stores' AND column_name = 'show_prices'
            """);

        Assert.Equal("NO|true", column);
    }

    [Fact]
    public async Task ANewStoreSavedWithShowPricesFalseReadsBackFalse()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await SaveStoreAsync(factory, ConfiguredStore(Tenant, showPrices: false));

        Assert.False((await ReloadStoreAsync(factory, Tenant)).ShowPrices);
        Assert.Equal(
            "false",
            await ScalarTextAsync(factory, $"SELECT show_prices::text AS \"Value\" FROM store.stores WHERE tenant_id = {Tenant}"));
    }

    [Fact]
    public async Task ANewStoreSavedWithDefaultsReadsBackTrue()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await SaveStoreAsync(factory, ConfiguredStore(Tenant));

        Assert.True((await ReloadStoreAsync(factory, Tenant)).ShowPrices);
    }

    [Fact]
    public async Task AStoreThatExistedBeforeTheMigrationShowsPrices()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var migrator = scope.ServiceProvider.GetRequiredService<StoreDbContext>().GetService<IMigrator>();
            await migrator.MigrateAsync("20261110031040_DropStoreOrderRequests", Ct);
            await InsertBareStoreAsync(factory, Tenant);
            await migrator.MigrateAsync(null, Ct);
        }

        Assert.Equal(
            "true",
            await ScalarTextAsync(factory, $"SELECT show_prices::text AS \"Value\" FROM store.stores WHERE tenant_id = {Tenant}"));
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
    public async Task TwoFirstCreatesOfTheSameTenantAreAConcurrencyConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await using var scope = factory.Services.CreateAsyncScope();
        await SaveStoreAsync(factory, ConfiguredStore(Tenant, slug: "acme"));
        scope.ServiceProvider.GetRequiredService<IOnlineStoreRepository>().Add(ConfiguredStore(Tenant, slug: "acme-2"));
        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => scope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", exception.Code);
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

    private static async Task SaveOrderConsentAsync(QepApiFactory factory, StoreOrderConsent consent)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IStoreOrderConsents>().Add(consent);
        await scope.ServiceProvider.GetRequiredService<IStoreUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task<StoreOrderConsent?> FindOrderConsentAsync(QepApiFactory factory, Guid orderId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStoreOrderConsents>().FindAsync(Tenant, orderId, Ct);
    }

    [Fact]
    public async Task AnOrderConsentRoundTrips()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var consent = StoreOrderConsent.Create(
            Guid.NewGuid(), Tenant, new StoreConsent(Now, "203.0.113.5", "https://acme.test/politica", Now.AddDays(-3)));
        await SaveOrderConsentAsync(factory, consent);

        var found = await FindOrderConsentAsync(factory, consent.OrderId);

        Assert.NotNull(found);
        Assert.Equal(consent.Consent, found.Consent);
        Assert.Equal(Now.AddDays(-3), found.Consent.PolicySetAt);
    }

    [Fact]
    public async Task AnOrderConsentWithoutPolicyRoundTripsWithNulls()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var consent = StoreOrderConsent.Create(Guid.NewGuid(), Tenant, new StoreConsent(Now, "203.0.113.5", null, null));
        await SaveOrderConsentAsync(factory, consent);

        var found = await FindOrderConsentAsync(factory, consent.OrderId);

        Assert.NotNull(found);
        Assert.Null(found.Consent.PolicyUrl);
        Assert.Null(found.Consent.PolicySetAt);
        Assert.Equal(consent.Consent, found.Consent);
    }

    [Fact]
    public async Task TheOrderConsentIsUniquePerOrder()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var orderId = Guid.NewGuid();
        var accepted = new StoreConsent(Now, "203.0.113.5", null, null);
        await SaveOrderConsentAsync(factory, StoreOrderConsent.Create(orderId, Tenant, accepted));

        // Segundo contexto: la base es la que rechaza el duplicado (en el mismo contexto EF fallaría antes, sin llegar a ella).
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => SaveOrderConsentAsync(factory, StoreOrderConsent.Create(orderId, Tenant, accepted)));

        Assert.True(UniqueViolation.Is(exception, "PK_order_consents"));
    }

    [Fact]
    public async Task TheRequestTablesAreGone()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var count = await ScalarAsync(
            factory,
            $"""
            SELECT count(*) AS "Value" FROM information_schema.tables
            WHERE table_schema = 'store' AND table_name IN ('order_requests', 'order_request_lines', 'request_counters')
            """);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task TheStoreModelHasNoRequestEntity()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();

        Assert.Null(db.Model.FindEntityType("Modules.Store.Domain.StoreOrderRequest"));
    }
}
