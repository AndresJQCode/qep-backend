using System.Globalization;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class PosSaleTests
{
    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void CreateComputesTheWorkedExampleToTheCent()
    {
        var session = OpenSession();

        var sale = Sale(session);

        Assert.Equal(34_042.86m, sale.Subtotal);
        Assert.Equal(3_847.14m, sale.TaxAmount);
        Assert.Equal(2_380m, sale.DiscountAmount);
        Assert.Equal(37_890m, sale.Total);
        Assert.Equal(2_110m, sale.ChangeAmount);
        Assert.Equal(PosSaleStatus.Completed, sale.Status);
        Assert.Equal(session.Id, sale.CashSessionId);
        Assert.Equal(session.CashierId, sale.CashierId);
        Assert.Equal(TenantId, sale.TenantId);
        Assert.Equal(Fingerprint, sale.RequestFingerprint);
        Assert.Equal(string.Empty, sale.SaleNumber);
        Assert.Equal<int>([1, 2, 3], sale.Lines.Select(line => line.Position));
        Assert.Equal(21_420m, sale.Lines[0].LineTotal);
        Assert.Collection(
            sale.Payments,
            card =>
            {
                Assert.Equal(PosPaymentMethod.Card, card.Method);
                Assert.Equal(20_000m, card.Amount);
                Assert.Null(card.Tendered);
                Assert.Equal("1234", card.Reference);
                Assert.Equal(1, card.Position);
            },
            cash =>
            {
                Assert.Equal(PosPaymentMethod.Cash, cash.Method);
                Assert.Equal(17_890m, cash.Amount);
                Assert.Equal(20_000m, cash.Tendered);
                Assert.Null(cash.Reference);
                Assert.Equal(2, cash.Position);
            });
        Assert.Equal(sale.Total, sale.Payments.Sum(payment => payment.Amount));
    }

    [Fact]
    public void TheSaleIsAlwaysToTheFinalConsumer()
    {
        var sale = Sale(OpenSession());

        Assert.Null(sale.CustomerId);
        Assert.Equal("Consumidor final", sale.CustomerName);
        Assert.Null(sale.CustomerIdentificationType);
        Assert.Equal("222222222222", sale.CustomerIdentificationNumber);
    }

    [Fact]
    public void TaxBreakdownGroupsByRateInOrder()
    {
        var sale = Sale(OpenSession());

        Assert.Equal<PosTaxBreakdownEntry>(
            [
                new PosTaxBreakdownEntry(0, 7_500m, 0m),
                new PosTaxBreakdownEntry(5, 8_542.86m, 427.14m),
                new PosTaxBreakdownEntry(19, 18_000m, 3_420m),
            ],
            sale.TaxBreakdown());
    }

    // Review Focus 2: addProduct sólo acumula sobre la línea sin descuento, así que el mismo
    // producto puede llegar en dos líneas.
    [Fact]
    public void TheSameProductOnTwoLinesKeepsBothLines()
    {
        var sale = Sale(
            OpenSession(),
            [
                new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 11_900m, 10m, 19),
                new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 11_900m, 0m, 19),
            ],
            [Cash(30_000m)]);

        Assert.Equal(2, sale.Lines.Count);
        Assert.Equal(10_710m + 11_900m, sale.Total);
    }

    [Fact]
    public void CardOnlyAndTransferOnlyNeedNoCashLine()
    {
        Assert.Equal(37_890m, Sale(OpenSession(), payments: [Card(37_890m)]).Total);
        Assert.Equal(0m, Sale(OpenSession(), payments: [Transfer(37_890m)]).ChangeAmount);
    }

    [Fact]
    public void ExactCashLeavesNoChange()
    {
        var sale = Sale(OpenSession(), payments: [Cash(37_890m)]);

        Assert.Equal(0m, sale.ChangeAmount);
        Assert.Equal(37_890m, sale.Payments.Single().Amount);
    }

    // Regla 8: con total 0, un único Cash con recibido 0, y es la única venta cuyo Cash guarda 0.
    [Fact]
    public void AZeroTotalTakesASingleCashWithZeroTendered()
    {
        var sale = Sale(
            OpenSession(),
            [new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 11_900m, 100m, 19)],
            [Cash(0m)]);

        Assert.Equal(0m, sale.Total);
        var cash = Assert.Single(sale.Payments);
        Assert.Equal(0m, cash.Amount);
        Assert.Equal(0m, cash.Tendered);
        Assert.Equal(0m, sale.ChangeAmount);
    }

    public static TheoryData<string, PosPaymentInput[]> RejectedPayments => new()
    {
        { "pos.sale.payment_required", [] },
        { "pos.sale.too_many_payments", [Card(1m), Card(1m), Card(1m), Card(1m), Card(1m), Cash(40_000m)] },
        { "pos.sale.duplicate_cash_payment", [Cash(20_000m), Cash(20_000m)] },
        { "pos.sale.payment_amount_invalid", [Card(0m), Cash(40_000m)] },
        { "pos.sale.payment_amount_invalid", [Card(1.005m), Cash(40_000m)] },
        { "pos.sale.payment_amount_invalid", [new PosPaymentInput(PosPaymentMethod.Card, null, null, null), Cash(40_000m)] },
        { "pos.sale.tendered_only_for_cash", [new PosPaymentInput(PosPaymentMethod.Card, 37_890m, 37_890m, null)] },
        { "pos.sale.tendered_invalid", [new PosPaymentInput(PosPaymentMethod.Cash, null, null, null)] },
        { "pos.sale.tendered_invalid", [Cash(-1m)] },
        { "pos.sale.tendered_invalid", [Cash(100_000_000.01m)] },
        { "pos.sale.tendered_invalid", [Cash(40_000.005m)] },
        { "pos.sale.cash_amount_not_allowed", [new PosPaymentInput(PosPaymentMethod.Cash, 37_890m, 40_000m, null)] },
        { "pos.sale.payment_exceeds_total", [Card(40_000m)] },
        { "pos.sale.payment_insufficient", [Cash(37_889.99m)] },
        { "pos.sale.payment_insufficient", [Card(20_000m)] },
        { "pos.sale.cash_payment_unneeded", [Card(37_890m), Cash(0m)] },
    };

    [Theory]
    [MemberData(nameof(RejectedPayments))]
    public void EachPaymentRuleRejectsWithItsCode(string code, PosPaymentInput[] payments)
    {
        var session = OpenSession();

        var error = Assert.Throws<PosDomainException>(() => Sale(session, payments: payments));

        Assert.Equal(code, error.Code);
    }

    // Regla 8 y decisión 46: una venta en cero no recibe billete ni da cambio. Create no toca la
    // caja, así que no hay nada más que comprobar que el código.
    [Fact]
    public void AZeroTotalWithTenderedIsRejected()
    {
        var error = Assert.Throws<PosDomainException>(() => Sale(
            OpenSession(),
            [new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 11_900m, 100m, 19)],
            [Cash(5_000m)]));

        Assert.Equal("pos.sale.tendered_invalid", error.Code);
    }

    [Fact]
    public void AZeroTotalPaidByCardExceedsTheTotal()
    {
        var error = Assert.Throws<PosDomainException>(() => Sale(
            OpenSession(),
            [new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 0m, 0m, 19)],
            [Card(1m)]));

        Assert.Equal("pos.sale.payment_exceeds_total", error.Code);
    }

    [Theory]
    [InlineData("0", "0", "pos.sale.quantity_invalid")]
    [InlineData("100000", "0", "pos.sale.quantity_invalid")]
    [InlineData("1.005", "0", "pos.sale.quantity_invalid")]
    [InlineData("1", "100.01", "pos.sale.discount_out_of_range")]
    [InlineData("1", "-1", "pos.sale.discount_out_of_range")]
    [InlineData("1", "7.005", "pos.sale.discount_out_of_range")]
    public void LinesRejectQuantityAndDiscountOutOfRangeOrScale(string quantity, string discount, string code)
    {
        var error = Assert.Throws<PosDomainException>(() => Sale(
            OpenSession(),
            [new(Shampoo, "SH-400", "Shampoo 400 ml", D(quantity), 11_900m, D(discount), 19)],
            [Cash(100_000_000m)]));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void ASaleNeedsBetweenOneAndTwoHundredLines()
    {
        var none = Assert.Throws<PosDomainException>(() => Sale(OpenSession(), [], [Cash(0m)]));
        var line = new PosSaleLineInput(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 1m, 0m, 0);
        var tooMany = Assert.Throws<PosDomainException>(() =>
            Sale(OpenSession(), Enumerable.Repeat(line, 201).ToArray(), [Cash(1_000m)]));

        Assert.Equal("pos.sale.lines_required", none.Code);
        Assert.Equal("pos.sale.too_many_lines", tooMany.Code);
    }

    [Fact]
    public void ACashLineIgnoresAReferenceAndTrimsCardReferences()
    {
        var sale = Sale(
            OpenSession(),
            payments: [Card(20_000m, "  1234  "), new PosPaymentInput(PosPaymentMethod.Cash, null, 20_000m, "x")]);

        Assert.Equal("1234", sale.Payments[0].Reference);
        Assert.Null(sale.Payments[1].Reference);
    }

    [Fact]
    public void AssignNumberFormatsAndCanOnlyHappenOnce()
    {
        var sale = Sale(OpenSession());

        sale.AssignNumber(42);

        Assert.Equal("POS-000042", sale.SaleNumber);
        Assert.Throws<InvalidOperationException>(() => sale.AssignNumber(43));
        Assert.Equal("POS-1000000", PosSaleNumber.Format(1_000_000));
    }

    [Fact]
    public void VoidRecordsWhoWhenAndWhyAndOnlyOnce()
    {
        var sale = Sale(OpenSession());
        var admin = new MemberId(Guid.CreateVersion7());

        sale.Void("  Cliente se arrepintió  ", admin, Now.AddMinutes(5));

        Assert.Equal(PosSaleStatus.Voided, sale.Status);
        Assert.Equal("Cliente se arrepintió", sale.VoidReason);
        Assert.Equal(admin, sale.VoidedBy);
        Assert.Equal(Now.AddMinutes(5), sale.VoidedAt);
        var again = Assert.Throws<PosDomainException>(() => sale.Void("Otra vez", admin, Now));
        Assert.Equal("pos.sale.already_voided", again.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void VoidNeedsAReason(string reason)
    {
        var sale = Sale(OpenSession());

        var error = Assert.Throws<PosDomainException>(() =>
            sale.Void(reason, new MemberId(Guid.CreateVersion7()), Now));

        Assert.Equal("pos.sale.void_reason_required", error.Code);
        Assert.Equal(PosSaleStatus.Completed, sale.Status);
    }

    [Theory]
    [InlineData(PosSaleStatus.Completed, CashSessionStatus.Open, true, null)]
    [InlineData(PosSaleStatus.Voided, CashSessionStatus.Open, false, "AlreadyVoided")]
    [InlineData(PosSaleStatus.Voided, CashSessionStatus.Closed, false, "AlreadyVoided")]
    [InlineData(PosSaleStatus.Completed, CashSessionStatus.Closed, false, "SessionClosed")]
    public void VoidabilityDescribesTheSaleNotTheCaller(
        PosSaleStatus status, CashSessionStatus sessionStatus, bool voidable, string? reason)
    {
        Assert.Equal((voidable, reason), PosVoidability.For(status, sessionStatus));
    }
}
