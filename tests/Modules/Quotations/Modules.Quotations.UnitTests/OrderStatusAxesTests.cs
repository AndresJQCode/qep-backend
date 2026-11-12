using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Los cuatro ejes del pedido (spec 2026-11-10): estado, pago, envío y facturado, y la guarda de edición.</summary>
public sealed class OrderStatusAxesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly MemberId ConvertedBy = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static OrderHeader Header() =>
        new(ClientId, Advisor, "COP", "Transferencia", false, null, false, false);

    private static OrderLineInput Line() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    // Una línea de 2 × 11.900 con IVA del 19 % → neto 23.800. Por defecto, un comprobante de 100.000.
    private static Order NewOrder(IReadOnlyCollection<OrderPaymentProofInput>? proofs = null) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", Header(), QuotationParties.Empty, Account,
            ShippingDetails.None, [Line()], null, ConvertedBy,
            proofs ?? [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)], Now);

    private static string CodeOf(Action action) => Assert.Throws<QuotationsDomainException>(action).Code;

    [Fact]
    public void ANewOrderIsOpenNotPreparedAndNotInvoiced()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(OrderFulfillmentStatus.NotPrepared, order.FulfillmentStatus);
        Assert.False(order.IsInvoiced);
        Assert.True(order.IsEditable);
    }

    [Fact]
    public void PreparingRecordsWhoAndWhenAndFreezesTheOrder()
    {
        var order = NewOrder();

        order.Prepare(Advisor, Now);

        Assert.Equal(OrderFulfillmentStatus.Prepared, order.FulfillmentStatus);
        Assert.Equal(Advisor, order.PreparedBy);
        Assert.Equal(Now, order.PreparedAt);
        Assert.False(order.IsEditable);
        Assert.Equal("order.order.not_editable", CodeOf(() => order.UpdateNotes("x", Now)));
    }

    [Fact]
    public void APaidOrderIsStillEditable()
    {
        var order = NewOrder();

        order.MarkPaid(Advisor, requirePaymentProof: false, Now);

        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Equal(Advisor, order.PaidMarkedBy);
        Assert.True(order.IsEditable);
    }

    [Fact]
    public void AnInvoicedOrderStaysOpenAndFrozen()
    {
        var order = NewOrder();

        order.Invoice(Advisor, requireFullPayment: false, Now);

        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.True(order.IsInvoiced);
        Assert.False(order.IsEditable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvoicingRequiresFullPaymentOnlyWhenTheTenantAsks(bool require)
    {
        var order = NewOrder();

        if (require)
        {
            Assert.Equal("order.order.not_paid", CodeOf(() => order.Invoice(Advisor, requireFullPayment: true, Now)));
        }
        else
        {
            order.Invoice(Advisor, requireFullPayment: false, Now);
            Assert.True(order.IsInvoiced);
        }
    }

    [Fact]
    public void RevertingTheInvoicingKeepsTheStatus()
    {
        var order = NewOrder();
        order.Invoice(Advisor, requireFullPayment: false, Now);
        order.Archive(Advisor, Now);

        order.RevertInvoicing(Advisor, Now);

        Assert.Equal(OrderStatus.Archived, order.Status);
        Assert.False(order.IsInvoiced);
    }

    [Fact]
    public void MarkingPaidTwiceIsAlreadyPaid()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);

        Assert.Equal(
            "order.order.already_paid",
            CodeOf(() => order.MarkPaid(Advisor, requirePaymentProof: false, Now)));
    }

    [Fact]
    public void MarkingPaidWithoutProofFailsWhenTheTenantRequiresOne()
    {
        var order = NewOrder(proofs: []);

        Assert.Equal(
            "order.order.payment_proof_required",
            CodeOf(() => order.MarkPaid(Advisor, requirePaymentProof: true, Now)));
    }

    [Fact]
    public void UnmarkingPaidIsRejectedOncePreparedOrInvoiced()
    {
        var prepared = NewOrder();
        prepared.MarkPaid(Advisor, requirePaymentProof: false, Now);
        prepared.Prepare(Advisor, Now);
        Assert.Equal("order.order.already_prepared", CodeOf(() => prepared.UnmarkPaid(Now)));

        var invoiced = NewOrder();
        invoiced.MarkPaid(Advisor, requirePaymentProof: false, Now);
        invoiced.Invoice(Advisor, requireFullPayment: true, Now);
        Assert.Equal("order.order.already_invoiced", CodeOf(() => invoiced.UnmarkPaid(Now)));
    }

    [Fact]
    public void UnmarkingPaidReturnsToTheCalculatedStatus()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);

        order.UnmarkPaid(Now);

        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
        Assert.Null(order.PaidMarkedAt);
        Assert.Null(order.PaidMarkedBy);
    }

    [Fact]
    public void UndoingThePreparationIsRejectedOnceInvoiced()
    {
        var order = NewOrder();
        order.Prepare(Advisor, Now);
        order.Invoice(Advisor, requireFullPayment: false, Now);

        Assert.Equal("order.order.already_invoiced", CodeOf(() => order.UndoPreparation(Now)));
    }

    [Fact]
    public void UndoingAPreparationThatDoesNotExistIsNotPrepared()
    {
        var order = NewOrder();

        Assert.Equal("order.order.not_prepared", CodeOf(() => order.UndoPreparation(Now)));
    }

    [Fact]
    public void ArchivingAndUnarchivingRoundTrips()
    {
        var order = NewOrder();

        order.Archive(Advisor, Now);
        Assert.Equal(OrderStatus.Archived, order.Status);
        Assert.Equal(Advisor, order.ArchivedBy);

        order.Unarchive(Now);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Null(order.ArchivedAt);
    }

    [Fact]
    public void ACancelledOrderCannotBeArchivedNorAnArchivedOneCancelled()
    {
        var cancelled = NewOrder();
        cancelled.Cancel(Advisor, "Duplicado", Now);
        Assert.Equal("order.order.already_cancelled", CodeOf(() => cancelled.Archive(Advisor, Now)));

        var archived = NewOrder();
        archived.Archive(Advisor, Now);
        Assert.Equal("order.order.already_archived", CodeOf(() => archived.Cancel(Advisor, "Duplicado", Now)));
    }

    [Fact]
    public void AnArchivedOrderCannotBePreparedOrMarkedPaid()
    {
        var order = NewOrder();
        order.Archive(Advisor, Now);

        Assert.Equal("order.order.not_open", CodeOf(() => order.Prepare(Advisor, Now)));
        Assert.Equal("order.order.not_open", CodeOf(() => order.MarkPaid(Advisor, requirePaymentProof: false, Now)));
    }

    [Fact]
    public void TheConsoleCannotCancelAPreparedOrder()
    {
        var order = NewOrder();
        order.Prepare(Advisor, Now);

        Assert.Equal("order.order.already_prepared", CodeOf(() => order.Cancel(Advisor, "Duplicado", Now)));
    }

    [Fact]
    public void TheConsoleChecksInvoicedBeforeEverythingElse()
    {
        var order = NewOrder();
        order.Prepare(Advisor, Now);
        order.Invoice(Advisor, requireFullPayment: false, Now);

        Assert.Equal("order.order.already_invoiced", CodeOf(() => order.Cancel(Advisor, "Duplicado", Now)));
    }

    [Fact]
    public void ThePlatformCancelWinsOverPreparedAndArchivedAndKeepsTheMarks()
    {
        var order = NewOrder();
        order.Prepare(Advisor, Now);
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);
        order.Archive(Advisor, Now);

        order.CancelFromPlatform(Advisor, "Anulado en Shopify", Now);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(OrderFulfillmentStatus.Prepared, order.FulfillmentStatus);
        Assert.Equal(Now, order.PreparedAt);
        Assert.Equal(Now, order.ArchivedAt);
    }

    [Fact]
    public void ThePlatformCancelStillRefusesAnInvoicedOrder()
    {
        var order = NewOrder();
        order.Invoice(Advisor, requireFullPayment: false, Now);

        Assert.Equal(
            "order.order.already_invoiced",
            CodeOf(() => order.CancelFromPlatform(Advisor, "Anulado en Shopify", Now)));
    }

    [Fact]
    public void ArchiveIfSettledArchivesOnlyWhenPaidAndPrepared()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);

        Assert.False(order.ArchiveIfSettled(Advisor, Now));
        Assert.Equal(OrderStatus.Open, order.Status);

        order.Prepare(Advisor, Now);

        Assert.True(order.ArchiveIfSettled(Advisor, Now));
        Assert.Equal(OrderStatus.Archived, order.Status);
        Assert.Equal(Advisor, order.ArchivedBy);
    }

    public static TheoryData<string, bool> BuyerCases => new()
    {
        { "nothing", true },
        { "prepare", false },
        { "invoice", false },
        { "markPaid", false },
        { "archive", false },
    };

    [Theory]
    [MemberData(nameof(BuyerCases))]
    public void TheBuyerCancelsOnlyAnUntouchedOpenOrder(string touch, bool expected)
    {
        var order = NewOrder(proofs: []);
        Action<Order> apply = touch switch
        {
            "prepare" => o => o.Prepare(Advisor, Now),
            "invoice" => o => o.Invoice(Advisor, requireFullPayment: false, Now),
            "markPaid" => o => o.MarkPaid(Advisor, requirePaymentProof: false, Now),
            "archive" => o => o.Archive(Advisor, Now),
            _ => _ => { },
        };

        apply(order);

        Assert.Equal(expected, order.CanBeCancelledByBuyer);
    }
}
