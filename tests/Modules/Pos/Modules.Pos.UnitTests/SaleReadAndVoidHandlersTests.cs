using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class SaleReadAndVoidHandlersTests
{
    private static readonly string[] Reader = [PosPermissions.SaleRead];
    private static readonly string[] Supervisor = [PosPermissions.SaleRead, PosPermissions.RegisterRead];
    private static readonly string[] Voider = [PosPermissions.SaleVoid];
    private static readonly string[] CardThenCash = ["Card", "Cash"];

    private static PosSale StoreSale(PosTestBed bed, CashSession session, DateTimeOffset? at = null)
    {
        var when = at ?? Now;
        var sale = PosSale.Create(
            PosSaleId.New(), Fingerprint, session, WorkedExampleLines(), [Card(20_000m, "1234"), Cash(20_000m)], when);
        sale.AssignNumber(bed.Sales.Stored.Count + 1);
        session.RegisterSale(sale, when);
        bed.Sales.Stored.Add(sale);
        return sale;
    }

    private static CashSession ForeignSession(PosTestBed bed)
    {
        var session = OpenSession(cashier: new MemberId(Guid.CreateVersion7()));
        bed.Sessions.Add(session);
        return session;
    }

    private static GetPosSaleHandler Get(PosTestBed bed, params string[] permissions) =>
        new(bed.Sales, bed.Sessions, bed.Cashiers, bed.Memberships, bed.Context(permissions), bed.TenantClock);

    private static ListPosSalesHandler List(PosTestBed bed, params string[] permissions) =>
        new(bed.Sales, bed.Memberships, bed.Context(permissions), bed.TenantClock, new ListPosSalesValidator());

    private static VoidPosSaleHandler Void(PosTestBed bed, params string[] permissions) =>
        new(bed.Sales, bed.Sessions, bed.UnitOfWork, bed.Audit, bed.Cashiers, bed.Memberships,
            bed.Context(permissions), bed.Clock, bed.TenantClock, new VoidPosSaleValidator());

    private static ListCashSessionsHandler Sessions(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Memberships, bed.Context(permissions), bed.TenantClock, new ListCashSessionsValidator());

    private static GetCashSessionHandler Session(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Memberships, bed.Context(permissions), bed.TenantClock);

    [Fact]
    public async Task TheCashierReadsTheirOwnSaleButNotSomeoneElses()
    {
        var bed = new PosTestBed();
        var own = StoreSale(bed, bed.OpenSessionInStore());
        var foreign = StoreSale(bed, ForeignSession(bed));

        var read = await Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, own.Id.Value), TestContext.Current.CancellationToken);
        var denied = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, foreign.Id.Value), TestContext.Current.CancellationToken));
        var supervisor = await Get(bed, Supervisor).HandleAsync(
            new GetPosSaleQuery(TenantId, foreign.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(own.SaleNumber, read.SaleNumber);
        Assert.Equal("authorization.denied", denied.Code);
        Assert.Equal(foreign.Id.Value, supervisor.Id);
    }

    [Fact]
    public async Task AnUnknownSaleIsNotFoundInsideTheTenant()
    {
        var bed = new PosTestBed();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, Guid.CreateVersion7()), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.not_found", error.Code);
    }

    // voidable describe la venta, no al que pregunta (spec, decisión 33).
    [Fact]
    public async Task VoidableFollowsTheSaleAndItsSessionEvenWithoutTheVoidPermission()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var sale = StoreSale(bed, session);

        var open = await Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, sale.Id.Value), TestContext.Current.CancellationToken);
        session.Close(117_890m, null, Now);
        var closed = await Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, sale.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal((true, (string?)null), (open.Voidable, open.VoidBlockedReason));
        Assert.Equal((false, (string?)"SessionClosed"), (closed.Voidable, closed.VoidBlockedReason));
    }

    [Fact]
    public async Task VoidSubtractsFromTheCountAuditsAndShowsWhoVoided()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var sale = StoreSale(bed, session);

        var voided = await Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, sale.Id.Value, "Cliente se arrepintió"), TestContext.Current.CancellationToken);

        Assert.Equal("Voided", voided.Status);
        Assert.Equal("Cliente se arrepintió", voided.Void!.Reason);
        Assert.Equal("Laura Gómez", voided.Void.VoidedByName);
        Assert.Equal((false, (string?)"AlreadyVoided"), (voided.Voidable, voided.VoidBlockedReason));
        Assert.Equal(0m, session.SalesTotal);
        Assert.Equal(1, session.VoidedCount);
        Assert.Equal(("pos.sale.voided", "pos_sale"), (Assert.Single(bed.Audit.Entries).Action, bed.Audit.Entries[0].ResourceType));
        Assert.Equal(1, bed.UnitOfWork.SaveCalls);
    }

    [Fact]
    public async Task VoidIsRejectedTwiceOnAClosedSessionWithoutPermissionAndForAnUnknownSale()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var twice = StoreSale(bed, session);
        await Void(bed, Voider).HandleAsync(new VoidPosSaleCommand(TenantId, twice.Id.Value, "Motivo"), TestContext.Current.CancellationToken);
        var late = StoreSale(bed, session);
        session.Close(117_890m, null, Now);

        var again = await Assert.ThrowsAsync<PosDomainException>(() => Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, twice.Id.Value, "Motivo"), TestContext.Current.CancellationToken));
        var closed = await Assert.ThrowsAsync<PosDomainException>(() => Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, late.Id.Value, "Motivo"), TestContext.Current.CancellationToken));
        var noPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() => Void(bed, Reader).HandleAsync(
            new VoidPosSaleCommand(TenantId, late.Id.Value, "Motivo"), TestContext.Current.CancellationToken));
        var unknown = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, Guid.CreateVersion7(), "Motivo"), TestContext.Current.CancellationToken));
        var shortReason = await Assert.ThrowsAsync<ValidationException>(() => Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, late.Id.Value, " ab "), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.already_voided", again.Code);
        Assert.Equal("pos.sale.void_session_closed", closed.Code);
        Assert.Equal("authorization.denied", noPermission.Code);
        Assert.Equal("pos.sale.not_found", unknown.Code);
        Assert.Contains(shortReason.Errors, failure => failure.PropertyName == "Reason");
        // Los rechazos no guardan ni auditan: sólo la primera anulación llegó a la base.
        Assert.Equal(1, bed.UnitOfWork.SaveCalls);
        Assert.Single(bed.Audit.Entries);
        Assert.Equal(1, session.VoidedCount);
    }

    [Fact]
    public async Task ListWithoutRegisterReadOnlyShowsTheCallersSalesEvenForAnotherSession()
    {
        var bed = new PosTestBed();
        var own = StoreSale(bed, bed.OpenSessionInStore());
        var foreignSession = ForeignSession(bed);
        StoreSale(bed, foreignSession);

        var mine = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, null, null, 1, 20), TestContext.Current.CancellationToken);
        var sneaky = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, foreignSession.Id.Value, null, null, null, null, 1, 20), TestContext.Current.CancellationToken);
        var all = await List(bed, Supervisor).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, null, null, 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal([own.Id.Value], mine.Items.Select(item => item.Id).ToArray());
        Assert.Empty(sneaky.Items);
        Assert.Equal(2, all.Total);
        var item = mine.Items[0];
        Assert.Equal(CardThenCash, item.PaymentMethods);
        Assert.Equal("Laura Gómez", item.CashierName);
        Assert.True(item.Voidable);
    }

    // Review Focus 3: 04:30 UTC del 8 son las 23:30 del 7 en Bogotá.
    [Fact]
    public async Task ListSalesCutsTheDayInTheTenantTimeZone()
    {
        var bed = new PosTestBed();
        bed.TenantClock.UtcNow = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var session = bed.OpenSessionInStore();
        var lateOnThe7th = StoreSale(bed, session, new DateTimeOffset(2026, 10, 8, 4, 30, 0, TimeSpan.Zero));
        StoreSale(bed, session, new DateTimeOffset(2026, 10, 8, 5, 30, 0, TimeSpan.Zero));
        var day = new DateOnly(2026, 10, 7);

        var page = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, day, day, null, null, 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal([lateOnThe7th.Id.Value], page.Items.Select(item => item.Id).ToArray());
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 23, 30, 0, TimeSpan.FromHours(-5)), page.Items[0].CreatedAtLocal);
        Assert.Equal(TimeSpan.FromHours(-5), page.Items[0].CreatedAtLocal.Offset);
    }

    [Fact]
    public async Task ListFiltersByStatusAndExactNumberAndValidatesItsInputs()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var first = StoreSale(bed, session);
        StoreSale(bed, session);
        await Void(bed, Voider).HandleAsync(new VoidPosSaleCommand(TenantId, first.Id.Value, "Motivo"), TestContext.Current.CancellationToken);

        var voided = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, "Voided", null, 1, 20), TestContext.Current.CancellationToken);
        var byNumber = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, null, "POS-000002", 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal([first.Id.Value], voided.Items.Select(item => item.Id).ToArray());
        Assert.Equal("POS-000002", Assert.Single(byNumber.Items).SaleNumber);
        await Assert.ThrowsAsync<ValidationException>(() => List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, "Anulada", null, 1, 20), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, null, null, 1, 101), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 7), null, null, 1, 20), TestContext.Current.CancellationToken));
    }

    // Decisión 32: el cajero lee sus propias cajas, abiertas y cerradas, para reimprimir un cierre.
    [Fact]
    public async Task TheCashierReadsTheirOwnClosedSessionButNotSomeoneElses()
    {
        var bed = new PosTestBed();
        var own = bed.OpenSessionInStore();
        own.Close(100_000m, null, Now);
        var foreign = ForeignSession(bed);

        var summary = await Session(bed, Reader).HandleAsync(
            new GetCashSessionQuery(TenantId, own.Id.Value), TestContext.Current.CancellationToken);
        var denied = await Assert.ThrowsAsync<RequestForbiddenException>(() => Session(bed, Reader).HandleAsync(
            new GetCashSessionQuery(TenantId, foreign.Id.Value), TestContext.Current.CancellationToken));
        var unknown = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Session(bed, Reader).HandleAsync(
            new GetCashSessionQuery(TenantId, Guid.CreateVersion7()), TestContext.Current.CancellationToken));
        var mine = await Sessions(bed, Reader).HandleAsync(
            new ListCashSessionsQuery(TenantId, null, null, null, 1, 20), TestContext.Current.CancellationToken);
        var everyone = await Sessions(bed, Supervisor).HandleAsync(
            new ListCashSessionsQuery(TenantId, null, null, null, 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal("Closed", summary.Status);
        Assert.Equal(100_000m, summary.ExpectedCash);
        Assert.Equal("authorization.denied", denied.Code);
        Assert.Equal("pos.session.not_found", unknown.Code);
        Assert.Equal([own.Id.Value], mine.Items.Select(item => item.Id).ToArray());
        Assert.Equal(2, everyone.Total);
    }
}
