using System.Globalization;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class CashSessionTests
{
    [Fact]
    public void OpenStartsAtVersionOneWithTheCompanySnapshotAndNoSales()
    {
        var session = OpenSession(100_000m);

        Assert.Equal(CashSessionStatus.Open, session.Status);
        Assert.Equal(1, session.Version);
        Assert.Equal(TenantId, session.TenantId);
        Assert.Equal(Cashier, session.CashierId);
        Assert.Equal("Laura Gómez", session.CashierName);
        Assert.Equal(Company.CompanyId, session.CompanyId);
        Assert.Equal("Origen Botánico SAS", session.CompanyName);
        Assert.Equal("900123456-1", session.CompanyTaxId);
        Assert.Equal("Cra 50 # 10-20, Rionegro", session.CompanyAddress);
        Assert.Equal("6045551234", session.CompanyPhone);
        Assert.Equal(100_000m, session.OpeningFloat);
        Assert.Equal(0, session.SalesCount);
        Assert.Equal(0m, session.SalesTotal);
        Assert.Equal(100_000m, session.LiveExpectedCash);
        Assert.Equal(Now, session.OpenedAt);
        Assert.Null(session.ExpectedCash);
        Assert.Null(session.ClosedAt);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("100000000.01")]
    [InlineData("10.005")]
    public void OpenRejectsAFloatOutOfRangeOrWithThreeDecimals(string openingFloat)
    {
        var error = Assert.Throws<PosDomainException>(() =>
            OpenSession(decimal.Parse(openingFloat, CultureInfo.InvariantCulture)));

        Assert.Equal("pos.session.opening_float_invalid", error.Code);
    }

    [Fact]
    public void OpenAcceptsAZeroFloatAndTheTopOne()
    {
        Assert.Equal(0m, OpenSession(0m).OpeningFloat);
        Assert.Equal(PosLimits.MaxCashAmount, OpenSession(PosLimits.MaxCashAmount).OpeningFloat);
    }

    // Faltante con signo negativo (spec, «Ejemplo trabajado»): la pantalla sólo elige el color.
    [Fact]
    public void CloseFreezesExpectedCountedAndASignedDifference()
    {
        var session = OpenSession(100_000m);
        var closedAt = Now.AddHours(8);

        session.Close(99_110m, "  Faltan 890  ", closedAt);

        Assert.Equal(CashSessionStatus.Closed, session.Status);
        Assert.Equal(100_000m, session.ExpectedCash);
        Assert.Equal(99_110m, session.CountedCash);
        Assert.Equal(-890m, session.CashDifference);
        Assert.Equal("Faltan 890", session.ClosingNote);
        Assert.Equal(closedAt, session.ClosedAt);
        Assert.Equal(2, session.Version);
    }

    [Fact]
    public void CloseWithABlankNoteStoresNoNote()
    {
        var session = OpenSession();

        session.Close(100_000m, "   ", Now);

        Assert.Null(session.ClosingNote);
        Assert.Equal(0m, session.CashDifference);
    }

    [Fact]
    public void ClosingAClosedSessionIsRejected()
    {
        var session = OpenSession();
        session.Close(100_000m, null, Now);

        var error = Assert.Throws<PosDomainException>(() => session.Close(1m, null, Now));

        Assert.Equal("pos.session.not_open", error.Code);
        Assert.Equal(100_000m, session.CountedCash);
        Assert.Equal(2, session.Version);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1000000000.01")]
    [InlineData("1.001")]
    public void CloseRejectsACountOutOfRangeOrWithThreeDecimals(string counted)
    {
        var session = OpenSession();

        var error = Assert.Throws<PosDomainException>(() =>
            session.Close(decimal.Parse(counted, CultureInfo.InvariantCulture), null, Now));

        Assert.Equal("pos.session.counted_cash_invalid", error.Code);
        Assert.Equal(CashSessionStatus.Open, session.Status);
        Assert.Equal(1, session.Version);
    }

    [Fact]
    public void RegisterSaleAddsByMethodAndMovesTheVersion()
    {
        var session = OpenSession(100_000m);
        var sale = Sale(session);

        session.RegisterSale(sale, Now);

        Assert.Equal(1, session.SalesCount);
        Assert.Equal(37_890m, session.SalesTotal);
        Assert.Equal(17_890m, session.CashTotal);
        Assert.Equal(20_000m, session.CardTotal);
        Assert.Equal(0m, session.TransferTotal);
        Assert.Equal(117_890m, session.LiveExpectedCash);
        Assert.Equal(2, session.Version);
    }

    // El arqueo resta lo aplicado en efectivo (17 890), no el billete (20 000).
    [Fact]
    public void RegisterVoidOfASplitSaleSubtractsTheAppliedCashNotTheTendered()
    {
        var session = OpenSession(100_000m);
        var sale = Sale(session);
        session.RegisterSale(sale, Now);
        sale.Void("Cliente se arrepintió", Cashier, Now);

        session.RegisterVoid(sale, Now);

        Assert.Equal(0, session.SalesCount);
        Assert.Equal(1, session.VoidedCount);
        Assert.Equal(0m, session.SalesTotal);
        Assert.Equal(0m, session.CashTotal);
        Assert.Equal(0m, session.CardTotal);
        Assert.Equal(100_000m, session.LiveExpectedCash);
        Assert.Equal(3, session.Version);
    }

    // Arrastre del ledger: con una venta que sí dio cambio, anular una y dejar otra viva.
    [Fact]
    public void ExpectedCashKeepsOnlyTheNetCashOfTheSalesThatStayAfterAVoid()
    {
        var session = OpenSession(100_000m);
        var kept = Sale(session, payments: [Cash(40_000m)]);
        var voided = Sale(session);
        session.RegisterSale(kept, Now);
        session.RegisterSale(voided, Now);
        voided.Void("Cliente se arrepintió", Cashier, Now);
        session.RegisterVoid(voided, Now);

        Assert.Equal(1, session.SalesCount);
        Assert.Equal(1, session.VoidedCount);
        Assert.Equal(37_890m, session.SalesTotal);
        Assert.Equal(37_890m, session.CashTotal);
        Assert.Equal(137_890m, session.LiveExpectedCash);

        session.Close(137_890m, null, Now);

        Assert.Equal(137_890m, session.ExpectedCash);
        Assert.Equal(0m, session.CashDifference);
    }

    [Fact]
    public void ClosingAfterASaleExpectsTheFloatPlusTheNetCash()
    {
        var session = OpenSession(100_000m);
        session.RegisterSale(Sale(session), Now);

        session.Close(117_000m, null, Now);

        Assert.Equal(117_890m, session.ExpectedCash);
        Assert.Equal(-890m, session.CashDifference);
    }

    [Fact]
    public void AClosedSessionTakesNoSaleAndNoVoid()
    {
        var session = OpenSession();
        var sale = Sale(session);
        session.RegisterSale(sale, Now);
        session.Close(117_890m, null, Now);

        var selling = Assert.Throws<PosDomainException>(() => session.RegisterSale(Sale(session), Now));
        var voiding = Assert.Throws<PosDomainException>(() => session.RegisterVoid(sale, Now));

        Assert.Equal("pos.session.not_open", selling.Code);
        Assert.Equal("pos.sale.void_session_closed", voiding.Code);
        Assert.Equal(1, session.SalesCount);
        Assert.Equal(0, session.VoidedCount);
        Assert.Equal(17_890m, session.CashTotal);
        Assert.Equal(3, session.Version);
    }

    [Fact]
    public void RegisterVoidRequiresTheSaleToBeVoided()
    {
        var session = OpenSession();
        var sale = Sale(session);
        session.RegisterSale(sale, Now);

        Assert.Throws<InvalidOperationException>(() => session.RegisterVoid(sale, Now));

        Assert.Equal(1, session.SalesCount);
        Assert.Equal(0, session.VoidedCount);
        Assert.Equal(17_890m, session.CashTotal);
        Assert.Equal(2, session.Version);
    }

    [Fact]
    public void RegisterSaleRequiresTheSaleToBeCompleted()
    {
        var session = OpenSession();
        var sale = Sale(session);
        sale.Void("Cliente se arrepintió", Cashier, Now);

        Assert.Throws<InvalidOperationException>(() => session.RegisterSale(sale, Now));

        Assert.Equal(0, session.SalesCount);
        Assert.Equal(0m, session.SalesTotal);
        Assert.Equal(1, session.Version);
    }

    [Fact]
    public void AVoidOfAnotherSessionsSaleIsRejected()
    {
        var session = OpenSession();
        var foreign = Sale(OpenSession());
        foreign.Void("Cliente se arrepintió", Cashier, Now);

        Assert.Throws<InvalidOperationException>(() => session.RegisterVoid(foreign, Now));
        Assert.Equal(0, session.VoidedCount);
        Assert.Equal(1, session.Version);
    }

    [Fact]
    public void ASaleOfAnotherSessionIsRejected()
    {
        var session = OpenSession();
        var foreign = Sale(OpenSession());

        Assert.Throws<InvalidOperationException>(() => session.RegisterSale(foreign, Now));
        Assert.Equal(0, session.SalesCount);
        Assert.Equal(1, session.Version);
    }
}
