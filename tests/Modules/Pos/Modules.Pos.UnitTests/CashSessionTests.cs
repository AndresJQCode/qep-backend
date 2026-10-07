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
}
