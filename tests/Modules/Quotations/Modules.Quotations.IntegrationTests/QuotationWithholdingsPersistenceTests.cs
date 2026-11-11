using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La foto de retenciones de la cotización ida y vuelta por el DbContext (spec impuestos por tipo §7.5, §8) y el
/// relleno de <c>billing_company_country</c> (plan P1, ruling C11).
/// </summary>
public sealed class QuotationWithholdingsPersistenceTests
{
    private const string LastMigrationBeforeWithholdings = "20261110023855_AddOrderBuyerAccount";

    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-00000000f011");
    private static readonly MemberId Advisor = new(Guid.Parse("01900000-0000-7000-8000-00000000f012"));
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount ColombianAccount = new()
    {
        CompanyId = Guid.Parse("01900000-0000-7000-8000-00000000f013"),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
        Country = "co",
    };

    private static readonly WithholdingRate Retefuente =
        new(Guid.CreateVersion7(), "Retefuente 2,5 %", "Retefuente", "CO", 2.5m, null, null, null);

    private static readonly WithholdingRate ReteIca =
        new(Guid.CreateVersion7(), "ReteICA Bogotá 0,966 %", "ReteICA", "CO", 0.966m, 4m, "UVT", 49_799m);

    private static readonly WithholdingRate RetefuenteServicios =
        new(Guid.CreateVersion7(), "Retefuente servicios 4 %", "Retefuente", "CO", 4m, null, null, null);

    private static readonly QuotationPartyDetails OwnBilling = new()
    {
        Name = "Sede administrativa",
        IdentificationNumber = "1020304050",
        IdentificationType = PartyIdentificationType.Nit,
    };

    [Fact]
    public async Task PhotoRoundTripsWithBothSources()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateAsync(connectionString);

        var quotation = NewQuotation([Retefuente, ReteIca], ColombianAccount);
        AddLine(quotation);
        UseOwnBilling(quotation);
        quotation.SetBillingWithholdings([RetefuenteServicios], Advisor, Now);
        await using (var create = NewContext(connectionString))
        {
            create.Quotations.Add(quotation);
            await new QuotationsUnitOfWork(create).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = NewContext(connectionString);
        var reloaded = await FindAsync(read, quotation.Id);

        Assert.Equal("CO", reloaded.BillingAccount!.Country);
        Assert.Equal([Retefuente, ReteIca], reloaded.CustomerWithholdings.Select(w => w.Rate));
        Assert.Equal([0, 1], reloaded.CustomerWithholdings.Select(w => (int)w.Position));
        Assert.All(reloaded.CustomerWithholdings, w => Assert.False(w.Applied));
        var selector = Assert.Single(reloaded.BillingWithholdings);
        Assert.Equal(RetefuenteServicios, selector.Rate);
        Assert.True(selector.Applied);
        Assert.Equal(100_000m, selector.BaseAmount);
        Assert.Equal(4_000m, selector.Amount);
        Assert.Equal(4_000m, reloaded.RetentionAmount);
    }

    // Lección de customer_tax_rates: sin ValueGeneratedNever en la clave, agregar una fila a un documento ya
    // rastreado sale como UPDATE de 0 filas (412) en vez de INSERT. Acá se agrega, se cambia y se quita.
    [Fact]
    public async Task WithholdingsCanBeAddedChangedAndRemovedOnAnExistingQuotation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateAsync(connectionString);

        var quotation = NewQuotation([], ColombianAccount);
        AddLine(quotation);
        await using (var create = NewContext(connectionString))
        {
            create.Quotations.Add(quotation);
            await new QuotationsUnitOfWork(create).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var add = NewContext(connectionString))
        {
            var tracked = await FindAsync(add, quotation.Id);
            UseOwnBilling(tracked);
            tracked.SetBillingWithholdings([Retefuente], Advisor, Now);
            await new QuotationsUnitOfWork(add).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal([(Retefuente.TaxRateId, 2_500m)], await BillingRowsAsync(connectionString, quotation.Id));

        await using (var change = NewContext(connectionString))
        {
            var tracked = await FindAsync(change, quotation.Id);
            tracked.SetBillingWithholdings([RetefuenteServicios, ReteIca], Advisor, Now);
            await new QuotationsUnitOfWork(change).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // ReteICA no aplica: su base (4 UVT) es 199.196 y la cotización tiene 100.000.
        Assert.Equal(
            [(RetefuenteServicios.TaxRateId, 4_000m), (ReteIca.TaxRateId, 0m)],
            await BillingRowsAsync(connectionString, quotation.Id));

        await using (var clear = NewContext(connectionString))
        {
            var tracked = await FindAsync(clear, quotation.Id);
            tracked.SetBillingWithholdings([], Advisor, Now);
            await new QuotationsUnitOfWork(clear).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(await BillingRowsAsync(connectionString, quotation.Id));
        await using var read = NewContext(connectionString);
        var reloaded = await FindAsync(read, quotation.Id);
        Assert.Equal(0m, reloaded.RetentionAmount);
        // Alta 1, línea 2, parte propia 3 y las tres ediciones de los selectores: cada una es una versión.
        Assert.Equal(6, reloaded.Version);
    }

    // Patrón de QuotationSettingsMigrationTests: baja a la migración anterior, deja una cotización con una
    // empresa de España y otra con una empresa que no está en companies.companies, y sube.
    [Fact]
    public async Task BillingCompanyCountryIsBackfilledFromCompanies()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateAsync(connectionString);

        var spanishCompanyId = Guid.CreateVersion7();
        var spanish = NewQuotation([], ColombianAccount with { CompanyId = spanishCompanyId });
        var unknown = NewQuotation([], ColombianAccount with { CompanyId = Guid.CreateVersion7() });
        await using (var create = NewContext(connectionString))
        {
            create.Quotations.AddRange(spanish, unknown);
            await new QuotationsUnitOfWork(create).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = NewContext(connectionString))
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(LastMigrationBeforeWithholdings, TestContext.Current.CancellationToken);

            // Sólo el esquema de Quotations migra acá: la tabla de empresas se arma a mano con las dos columnas
            // que lee el relleno.
            await ExecuteAsync(
                connectionString,
                "CREATE SCHEMA IF NOT EXISTS companies; " +
                "CREATE TABLE companies.companies (id uuid PRIMARY KEY, country varchar(2) NOT NULL); " +
                $"INSERT INTO companies.companies (id, country) VALUES ('{spanishCompanyId}', 'ES');");

            await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
        }

        await using var read = NewContext(connectionString);
        var countries = await read.Quotations
            .Where(q => q.Id == spanish.Id || q.Id == unknown.Id)
            .ToDictionaryAsync(q => q.Id, q => q.BillingAccount!.Country, TestContext.Current.CancellationToken);
        Assert.Equal("ES", countries[spanish.Id]);
        Assert.Equal("CO", countries[unknown.Id]);
    }

    // Sin el esquema de empresas (una base donde Companies no migró), el relleno no falla: todas quedan en CO.
    [Fact]
    public async Task BillingCompanyCountryFallsBackToColombiaWithoutTheCompaniesSchema()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateAsync(connectionString);

        var quotation = NewQuotation([], ColombianAccount);
        await using (var context = NewContext(connectionString))
        {
            context.Quotations.Add(quotation);
            await new QuotationsUnitOfWork(context).SaveChangesAsync(TestContext.Current.CancellationToken);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(LastMigrationBeforeWithholdings, TestContext.Current.CancellationToken);
            await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
        }

        await using var read = NewContext(connectionString);
        var reloaded = await FindAsync(read, quotation.Id);
        Assert.Equal("CO", reloaded.BillingAccount!.Country);
    }

    private static Quotation NewQuotation(IReadOnlyList<WithholdingRate> customerWithholdings, QuotationBillingAccount account) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            $"COT-{Guid.CreateVersion7():N}"[..20],
            Guid.CreateVersion7(),
            Advisor,
            new DateOnly(2026, 12, 31),
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            account,
            "COP",
            customerVatSurplus: false,
            customerWithholdings,
            Advisor,
            Now);

    // 119.000 con el IVA del 19 % adentro: base sin IVA 100.000.
    private static void AddLine(Quotation quotation) =>
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, Advisor, Now);

    private static void UseOwnBilling(Quotation quotation) =>
        quotation.UpdateDetails(
            quotation.ValidUntil, null, null, new QuotationParties(OwnBilling, null, BillingVatSurplus: false),
            quotation.BillingAccount, null, isRetail: false, globalScaleFloor: null, Advisor, Now);

    // Como QuotationRepository.FindAsync: con las líneas y las partes, que el recálculo necesita. La foto de
    // retenciones es owned y llega sola.
    private static Task<Quotation> FindAsync(QuotationsDbContext context, QuotationId id) =>
        context.Quotations
            .Include(q => q.Items)
            .Include(q => q.Parties)
            .AsSingleQuery()
            .SingleAsync(q => q.Id == id, TestContext.Current.CancellationToken);

    private static async Task<List<(Guid TaxRateId, decimal Amount)>> BillingRowsAsync(
        string connectionString, QuotationId quotationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT tax_rate_id, amount FROM quotations.quotation_withholdings " +
            "WHERE document_id = @id AND source = 'Billing' ORDER BY position",
            connection);
        command.Parameters.AddWithValue("id", quotationId.Value);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(Guid, decimal)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetGuid(0), reader.GetDecimal(1)));
        }

        return rows;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task MigrateAsync(string connectionString)
    {
        await using var setup = NewContext(connectionString);
        await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);
}
