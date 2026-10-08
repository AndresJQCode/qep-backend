using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pos.Domain;
using Modules.Pos.Infrastructure.Persistence;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// Lo que sólo la base hace cumplir (spec 2026-10-07, «Persistencia» y «Traducción de errores»):
/// el índice parcial de una caja abierta por cajero, el choque determinista cierre/venta sobre la
/// Version, el contador sin huecos y la PK de la venta.
/// </summary>
public sealed class PosPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);

    private static readonly int[] ExpectedPositions = [1, 2];

    private static readonly PosPaymentMethod[] ExpectedMethods = [PosPaymentMethod.Card, PosPaymentMethod.Cash];

    private static PosSaleLineInput Line(decimal price = 10_000m) =>
        new(Guid.CreateVersion7(), "SH-400", "Shampoo 400 ml", 1m, price, 0m, 19);

    private static async Task<(Guid TenantId, PosCompanySnapshot Company)> ArrangeTenantAsync(QepApiFactory factory)
    {
        var tenant = await RegisterTenantAsync(factory);
        var company = await CreateCompanyAsync(tenant.Seeder, tenant.TenantId);
        return (tenant.TenantId, new PosCompanySnapshot(company.Id, company.Name, company.TaxId, null, null));
    }

    private static CashSession NewSession(Guid tenantId, PosCompanySnapshot company, MemberId cashier) =>
        CashSession.Open(CashSessionId.New(), tenantId, cashier, "Laura Gómez", company, "COP", 100_000m, Now);

    [Fact]
    public async Task TwoOpenSessionsForTheSameCashierAreRejectedByThePartialIndex()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var cashier = new MemberId(Guid.CreateVersion7());

        using (var first = factory.Services.CreateScope())
        {
            var db = first.ServiceProvider.GetRequiredService<PosDbContext>();
            db.CashSessions.Add(NewSession(tenantId, company, cashier));
            await new PosUnitOfWork(db).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var second = factory.Services.CreateScope();
        var other = second.ServiceProvider.GetRequiredService<PosDbContext>();
        other.CashSessions.Add(NewSession(tenantId, company, cashier));

        var error = await Assert.ThrowsAsync<PosDomainException>(
            () => new PosUnitOfWork(other).SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("pos.session.already_open", error.Code);
    }

    [Fact]
    public async Task AClosedSessionDoesNotBlockOpeningAnother()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var cashier = new MemberId(Guid.CreateVersion7());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var closed = NewSession(tenantId, company, cashier);
        closed.Close(100_000m, null, Now);
        db.CashSessions.Add(closed);
        db.CashSessions.Add(NewSession(tenantId, company, cashier));

        // Dos filas escritas: el índice es parcial (status = 'Open') y la cerrada no cuenta.
        Assert.Equal(2, await new PosUnitOfWork(db).SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // Spec, «Concurrencia cierre/venta, determinista»: A carga la caja, B vende y commitea, A cierra
    // y guarda. A tiene que chocar; si no, el arqueo de A dejaría la venta de B afuera.
    [Fact]
    public async Task ACloseLoadedBeforeASaleCommittedConflictsAndTheCountKeepsTheSale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var session = NewSession(tenantId, company, new MemberId(Guid.CreateVersion7()));
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<PosDbContext>();
            db.CashSessions.Add(session);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<PosDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<PosDbContext>();
        var closing = await dbA.CashSessions.SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
        var selling = await dbB.CashSessions.SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);

        var sale = PosSale.Create(
            PosSaleId.New(), new string('b', 64), selling, [Line(10_000m)],
            [new PosPaymentInput(PosPaymentMethod.Cash, null, 20_000m, null)], Now);
        sale.AssignNumber(1);
        dbB.Sales.Add(sale);
        selling.RegisterSale(sale, Now);
        await new PosUnitOfWork(dbB).SaveChangesAsync(TestContext.Current.CancellationToken);

        closing.Close(100_000m, null, Now);
        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => new PosUnitOfWork(dbA).SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("concurrency.conflict", error.Code);

        using var scopeC = factory.Services.CreateScope();
        var dbC = scopeC.ServiceProvider.GetRequiredService<PosDbContext>();
        var reloaded = await dbC.CashSessions.SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
        Assert.Equal(CashSessionStatus.Open, reloaded.Status);
        Assert.Equal(1, reloaded.SalesCount);
        reloaded.Close(110_000m, null, Now);
        await dbC.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(110_000m, reloaded.ExpectedCash);
        Assert.Equal(0m, reloaded.CashDifference);
    }

    [Fact]
    public async Task TheCounterNumbersInOrderAndARolledBackNumberIsReused()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var generator = new PosSaleNumberGenerator(db);

        Assert.Equal(1, await generator.NextAsync(tenantId, TestContext.Current.CancellationToken));
        Assert.Equal(2, await generator.NextAsync(tenantId, TestContext.Current.CancellationToken));
        await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(3, await generator.NextAsync(tenantId, TestContext.Current.CancellationToken));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // Un 422 o un 412 dentro de la transacción no gastan número (spec, «Crear venta», paso 7).
        Assert.Equal(3, await generator.NextAsync(tenantId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASaleIdAlreadyUsedIsTranslatedToIdTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var saleId = PosSaleId.New();
        var cash = new PosPaymentInput(PosPaymentMethod.Cash, null, 20_000m, null);

        using (var first = factory.Services.CreateScope())
        {
            var db = first.ServiceProvider.GetRequiredService<PosDbContext>();
            var session = NewSession(tenantId, company, new MemberId(Guid.CreateVersion7()));
            var sale = PosSale.Create(saleId, new string('c', 64), session, [Line()], [cash], Now);
            sale.AssignNumber(1);
            db.CashSessions.Add(session);
            db.Sales.Add(sale);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Otro tenant (sin FK a tenancy: tenant_id es un id suelto), otra caja, el mismo id de venta.
        using var second = factory.Services.CreateScope();
        var other = second.ServiceProvider.GetRequiredService<PosDbContext>();
        var otherSession = NewSession(Guid.CreateVersion7(), company, new MemberId(Guid.CreateVersion7()));
        other.CashSessions.Add(otherSession);
        await other.SaveChangesAsync(TestContext.Current.CancellationToken);
        var duplicate = PosSale.Create(saleId, new string('d', 64), otherSession, [Line()], [cash], Now);
        duplicate.AssignNumber(1);
        other.Sales.Add(duplicate);

        var error = await Assert.ThrowsAsync<PosDomainException>(
            () => new PosUnitOfWork(other).SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("pos.sale.id_taken", error.Code);
    }

    [Fact]
    public async Task ASaleRoundTripsWithItsLinesPaymentsAndVoid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var session = NewSession(tenantId, company, new MemberId(Guid.CreateVersion7()));
        var sale = PosSale.Create(
            PosSaleId.New(), new string('e', 64), session,
            [Line(11_900m), new(Guid.CreateVersion7(), "AV-01", "Avena granel (kg)", 1.5m, 5_000m, 0m, 0)],
            [new PosPaymentInput(PosPaymentMethod.Card, 10_000m, null, "1234"), new PosPaymentInput(PosPaymentMethod.Cash, null, 10_000m, null)],
            Now);
        sale.AssignNumber(7);
        var voidedBy = new MemberId(Guid.CreateVersion7());
        using (var write = factory.Services.CreateScope())
        {
            var db = write.ServiceProvider.GetRequiredService<PosDbContext>();
            db.CashSessions.Add(session);
            db.Sales.Add(sale);
            session.RegisterSale(sale, Now);
            sale.Void("Cliente se arrepintió", voidedBy, Now);
            session.RegisterVoid(sale, Now);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var read = factory.Services.CreateScope();
        var loaded = await read.ServiceProvider.GetRequiredService<PosDbContext>().Sales
            .SingleAsync(s => s.Id == sale.Id, TestContext.Current.CancellationToken);
        Assert.Equal("POS-000007", loaded.SaleNumber);
        Assert.Equal(19_400m, loaded.Total);
        Assert.Equal(ExpectedPositions, loaded.Lines.OrderBy(l => l.Position).Select(l => l.Position));
        Assert.Equal(1.5m, loaded.Lines.Single(l => l.Position == 2).Quantity);
        Assert.Equal(ExpectedMethods, loaded.Payments.OrderBy(p => p.Position).Select(p => p.Method));
        Assert.Equal(PosSaleStatus.Voided, loaded.Status);
        Assert.Equal(voidedBy, loaded.VoidedBy);
        Assert.Equal(new string('e', 64), loaded.RequestFingerprint);
    }
}
