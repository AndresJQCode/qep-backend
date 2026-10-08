using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class RegisterHandlersTests
{
    private static readonly string[] Operator = [PosPermissions.RegisterOperate];

    private static GetRegisterContextHandler Context(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Companies, bed.Cashiers, bed.Memberships, bed.Context(permissions), bed.TenantClock);

    private static OpenCashSessionHandler Open(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Companies, bed.Cashiers, bed.UnitOfWork, bed.Audit, bed.Memberships,
            bed.Context(permissions), bed.Clock, bed.TenantClock, bed.DefaultCurrency, new OpenCashSessionValidator());

    private static CloseCashSessionHandler Close(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.UnitOfWork, bed.Audit, bed.Memberships, bed.Context(permissions),
            bed.Clock, bed.TenantClock, new CloseCashSessionValidator());

    [Fact]
    public async Task RegisterContextWithoutASessionOffersTheOnlyActiveCompanyAsDefault()
    {
        var bed = new PosTestBed();
        var company = bed.AddCompany();
        bed.AddCompany("Inactiva SAS", active: false);

        var context = await Context(bed, Operator).HandleAsync(
            new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Null(context.Session);
        Assert.Equal(new PosCashierResponse(Cashier.Value, "Laura Gómez"), context.Cashier);
        Assert.Equal(company.Id, context.DefaultCompanyId);
        Assert.Equal([company.Id], context.Companies.Select(option => option.Id).ToArray());
    }

    [Fact]
    public async Task RegisterContextWithTwoActiveCompaniesHasNoDefault()
    {
        var bed = new PosTestBed();
        bed.AddCompany("Una SAS");
        bed.AddCompany("Otra SAS");

        var context = await Context(bed, Operator).HandleAsync(
            new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Null(context.DefaultCompanyId);
        Assert.Equal(2, context.Companies.Count);
    }

    [Fact]
    public async Task RegisterContextCarriesTheOpenSessionWithItsVersionAndTheThreeMethods()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        session.RegisterSale(Sale(session), Now);

        var context = await Context(bed, Operator).HandleAsync(
            new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(context.Session);
        Assert.Equal(session.Id.Value, context.Session.Id);
        Assert.Equal("Open", context.Session.Status);
        Assert.Equal(2, context.Session.Version);
        Assert.Equal(117_890m, context.Session.ExpectedCash);
        Assert.Equal(
            new[] { new PosPaymentTotalResponse("Cash", 17_890m), new PosPaymentTotalResponse("Card", 20_000m), new PosPaymentTotalResponse("Transfer", 0m) },
            context.Session.PaymentTotals);
        Assert.Equal(TimeSpan.FromHours(-5), context.Session.OpenedAtLocal.Offset);
        Assert.False(context.Session.OpenedBeforeToday);
    }

    // 04:00 UTC del 7 son las 23:00 del 6 en Bogotá: la caja quedó abierta desde ayer aunque la
    // fecha UTC sea la misma (spec, endpoint 1, openedBeforeToday).
    [Fact]
    public async Task RegisterContextFlagsASessionOpenedYesterdayInTheTenantTimeZone()
    {
        var bed = new PosTestBed();
        var session = CashSession.Open(
            CashSessionId.New(), TenantId, Cashier, "Laura Gómez", Company, "COP", 100_000m,
            new DateTimeOffset(2026, 10, 7, 4, 0, 0, TimeSpan.Zero));
        bed.Sessions.Add(session);

        var context = await Context(bed, Operator).HandleAsync(
            new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.True(context.Session!.OpenedBeforeToday);
    }

    [Fact]
    public async Task RegisterContextNeedsTheOperatePermissionAndTheRouteTenant()
    {
        var bed = new PosTestBed();

        var withoutPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Context(bed, PosPermissions.SaleRead).HandleAsync(new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken));
        var otherTenant = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Context(bed, Operator).HandleAsync(new GetRegisterContextQuery(Guid.CreateVersion7()), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", withoutPermission.Code);
        Assert.Equal("authorization.denied", otherTenant.Code);
    }

    [Fact]
    public async Task OpenUsesTheOnlyActiveCompanyAndAudits()
    {
        var bed = new PosTestBed();
        var company = bed.AddCompany();

        var opened = await Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 100_000m), TestContext.Current.CancellationToken);

        var session = Assert.Single(bed.Sessions.Sessions);
        Assert.Equal(company.Id, session.CompanyId);
        Assert.Equal("Laura Gómez", session.CashierName);
        Assert.Equal(Cashier, session.CashierId);
        Assert.Equal(1, opened.Version);
        Assert.Equal(100_000m, opened.ExpectedCash);
        Assert.Equal(1, bed.UnitOfWork.SaveCalls);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal(("pos.session.opened", "cash_session", session.Id.ToString()), (audit.Action, audit.ResourceType, audit.ResourceId));
        Assert.Equal(PosTestBed.UserId, audit.ActorId);
    }

    [Fact]
    public async Task OpenReadsTheTenantDefaultCurrencyOnce()
    {
        var currency = new FakeTenantDefaultCurrency("USD");
        var bed = new PosTestBed { DefaultCurrency = currency };
        bed.AddCompany();

        var response = await Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken);

        Assert.Equal("USD", response.Currency);
        Assert.Equal("USD", Assert.Single(bed.Sessions.Sessions).Currency);
        Assert.Equal([TenantId], currency.RequestedTenantIds);
    }

    [Fact]
    public async Task OpenWithTwoActiveCompaniesNeedsOneAndUsesTheChosenOne()
    {
        var bed = new PosTestBed();
        bed.AddCompany("Una SAS");
        var chosen = bed.AddCompany("Otra SAS");

        var missing = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken));
        await Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, chosen.Id, 0m), TestContext.Current.CancellationToken);

        Assert.Equal("pos.session.company_required", missing.Code);
        Assert.Equal(chosen.Id, Assert.Single(bed.Sessions.Sessions).CompanyId);
    }

    [Fact]
    public async Task OpenRejectsMissingForeignAndInactiveCompanies()
    {
        var bed = new PosTestBed();
        var foreign = bed.AddCompany("Ajena SAS", tenantId: Guid.CreateVersion7());
        var inactive = bed.AddCompany("Inactiva SAS", active: false);

        var none = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken));
        var other = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, foreign.Id, 0m), TestContext.Current.CancellationToken));
        var off = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, inactive.Id, 0m), TestContext.Current.CancellationToken));

        Assert.Equal("pos.session.no_active_company", none.Code);
        Assert.Equal("pos.session.company_not_found", other.Code);
        Assert.Equal("pos.session.company_inactive", off.Code);
        Assert.Empty(bed.Sessions.Sessions);
    }

    [Fact]
    public async Task OpenWithASessionAlreadyOpenIsRejected()
    {
        var bed = new PosTestBed();
        bed.AddCompany();
        bed.OpenSessionInStore();

        var error = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken));

        Assert.Equal("pos.session.already_open", error.Code);
        Assert.Equal(0, bed.UnitOfWork.SaveCalls);
    }

    [Fact]
    public async Task OpenValidatesTheFloatScaleBeforeTouchingAnything()
    {
        var bed = new PosTestBed();
        bed.AddCompany();

        await Assert.ThrowsAsync<ValidationException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 10.005m), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 100_000_000.01m), TestContext.Current.CancellationToken));

        Assert.Empty(bed.Sessions.Sessions);
    }

    [Fact]
    public async Task OpenWithoutAnActiveMembershipIsForbidden()
    {
        var bed = new PosTestBed();
        bed.Memberships.Active.Clear();
        bed.AddCompany();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task CloseReturnsTheSummaryWithASignedDifferenceAndAudits()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        session.RegisterSale(Sale(session), Now);

        var summary = await Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 2, 117_000m, "Faltan 890"),
            TestContext.Current.CancellationToken);

        Assert.Equal("Closed", summary.Status);
        Assert.Equal(117_890m, summary.ExpectedCash);
        Assert.Equal(117_000m, summary.CountedCash);
        Assert.Equal(-890m, summary.CashDifference);
        Assert.Equal("Faltan 890", summary.Note);
        Assert.Equal(1, bed.UnitOfWork.SaveCalls);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal(("pos.session.closed", "cash_session", session.Id.ToString()), (audit.Action, audit.ResourceType, audit.ResourceId));
        Assert.Equal(PosTestBed.UserId, audit.ActorId);
    }

    [Fact]
    public async Task ClosingAnAlreadyClosedSessionIsNotOpen()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        await Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, session.Version, 100_000m, null),
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PosDomainException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, session.Version, 100_000m, null),
            TestContext.Current.CancellationToken));

        Assert.Equal("pos.session.not_open", error.Code);
        Assert.Equal(1, bed.UnitOfWork.SaveCalls);
        Assert.Single(bed.Audit.Entries);
    }

    [Fact]
    public async Task OpenNeedsTheOperatePermissionAndTheRouteTenant()
    {
        var bed = new PosTestBed();
        bed.AddCompany();

        var withoutPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Open(bed, PosPermissions.SaleRead).HandleAsync(
                new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken));
        var otherTenant = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Open(bed, Operator).HandleAsync(
                new OpenCashSessionCommand(Guid.CreateVersion7(), null, 0m), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", withoutPermission.Code);
        Assert.Equal("authorization.denied", otherTenant.Code);
        Assert.Empty(bed.Sessions.Sessions);
        Assert.Equal(0, bed.UnitOfWork.SaveCalls);
    }

    // Autorizar antes de validar: sin permiso, un cuerpo inválido da 403 y no el mapa de errores.
    [Fact]
    public async Task OpenAuthorizesBeforeValidating()
    {
        var bed = new PosTestBed();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Open(bed, PosPermissions.SaleRead).HandleAsync(
                new OpenCashSessionCommand(TenantId, null, 10.005m), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task CloseNeedsTheOperatePermissionAndTheRouteTenant()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();

        var withoutPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Close(bed, PosPermissions.SaleRead).HandleAsync(
                new CloseCashSessionCommand(TenantId, session.Id.Value, 1, 0m, null), TestContext.Current.CancellationToken));
        var otherTenant = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Close(bed, Operator).HandleAsync(
                new CloseCashSessionCommand(Guid.CreateVersion7(), session.Id.Value, 1, 0m, null), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", withoutPermission.Code);
        Assert.Equal("authorization.denied", otherTenant.Code);
        Assert.Equal(CashSessionStatus.Open, session.Status);
        Assert.Equal(0, bed.UnitOfWork.SaveCalls);
    }

    // El cajero cierra contra el arqueo que vio (spec, «Cerrar caja», paso 2).
    [Fact]
    public async Task CloseWithAStaleVersionIs412AndLeavesTheSessionOpen()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        session.RegisterSale(Sale(session), Now);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 1, 117_890m, null),
            TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(CashSessionStatus.Open, session.Status);
        Assert.Equal(0, bed.UnitOfWork.SaveCalls);
    }

    [Fact]
    public async Task CloseSomeoneElsesSessionIsForbiddenAndAnUnknownOneIsNotFound()
    {
        var bed = new PosTestBed();
        var foreign = OpenSession(cashier: new MemberId(Guid.CreateVersion7()));
        bed.Sessions.Add(foreign);

        var forbidden = await Assert.ThrowsAsync<RequestForbiddenException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, foreign.Id.Value, 1, 0m, null), TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, Guid.CreateVersion7(), 1, 0m, null), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", forbidden.Code);
        Assert.Equal("pos.session.not_found", missing.Code);
    }

    // Un campo ausente en el JSON no debe llegar como 0: congelaría un faltante falso en el arqueo.
    [Fact]
    public async Task OpenAndCloseRejectAMissingAmountWithTheFieldInTheErrors()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();

        var open = await Assert.ThrowsAsync<ValidationException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, null), TestContext.Current.CancellationToken));
        var close = await Assert.ThrowsAsync<ValidationException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 1, null, null), TestContext.Current.CancellationToken));

        Assert.Contains(open.Errors, failure => failure.PropertyName == "OpeningFloat");
        Assert.Contains(close.Errors, failure => failure.PropertyName == "CountedCash");
    }

    [Fact]
    public async Task CloseValidatesTheCountAndTheNote()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();

        await Assert.ThrowsAsync<ValidationException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 1, 1_000_000_000.01m, null), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 1, 1.001m, null), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 1, 0m, new string('x', 501)), TestContext.Current.CancellationToken));
    }
}
