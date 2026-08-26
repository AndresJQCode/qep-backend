using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class SaleTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly QuotationId QuotationId = Domain.QuotationId.New();
    private static readonly MemberId ConvertedBy = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static Sale NewSale(
        SalePaymentStatus paymentStatus = SalePaymentStatus.FullPaymentReceived,
        IReadOnlyCollection<SalePaymentProofInput>? proofs = null) =>
        Sale.Create(
            SaleId.New(),
            TenantId,
            "VEN-2026-0001",
            QuotationId,
            paymentStatus,
            notes: null,
            ConvertedBy,
            proofs ?? [new SalePaymentProofInput(Guid.CreateVersion7(), 100_000m)],
            Now);

    [Fact]
    public void CreateStartsApprovedWithItsProofs()
    {
        var fileId = Guid.CreateVersion7();
        var sale = NewSale(proofs: [new SalePaymentProofInput(fileId, 50_000m)]);

        Assert.Equal(SaleStatus.Approved, sale.Status);
        Assert.Equal(QuotationId, sale.QuotationId);
        Assert.Equal(ConvertedBy, sale.ConvertedBy);
        Assert.Equal(Now, sale.ConvertedAt);
        Assert.Null(sale.RitualCollectionSyncId);
        Assert.Equal(1, sale.Version);
        var proof = Assert.Single(sale.PaymentProofs);
        Assert.Equal(fileId, proof.FileId);
        Assert.Equal(50_000m, proof.Amount);
        Assert.Equal(ConvertedBy, proof.UploadedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankSaleNumber(string number)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Sale.Create(
                SaleId.New(), TenantId, number, QuotationId, SalePaymentStatus.FullPaymentReceived,
                null, ConvertedBy, [new SalePaymentProofInput(Guid.CreateVersion7(), 1m)], Now));

        Assert.Equal("sale.sale.number_required", error.Code);
    }

    // US-14: se requiere al menos un comprobante, salvo que el pago quede pendiente.
    [Fact]
    public void CreateRequiresAtLeastOneProofUnlessPaymentIsPending()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewSale(paymentStatus: SalePaymentStatus.PartialPaymentReceived, proofs: []));

        Assert.Equal("sale.sale.payment_proof_required", error.Code);
    }

    [Fact]
    public void CreateAllowsNoProofsWhenPaymentIsPending()
    {
        var sale = NewSale(paymentStatus: SalePaymentStatus.PaymentPending, proofs: []);

        Assert.Empty(sale.PaymentProofs);
        Assert.Equal(SalePaymentStatus.PaymentPending, sale.PaymentStatus);
    }

    [Fact]
    public void CreateRejectsAProofWithoutAFile()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewSale(proofs: [new SalePaymentProofInput(Guid.Empty, 100m)]));

        Assert.Equal("sale.payment_proof.file_required", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateRejectsAProofWithANonPositiveAmount(decimal amount)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewSale(proofs: [new SalePaymentProofInput(Guid.CreateVersion7(), amount)]));

        Assert.Equal("sale.payment_proof.amount_invalid", error.Code);
    }

    [Fact]
    public void CreateAcceptsSeveralProofsWithDifferentAmounts()
    {
        var sale = NewSale(proofs:
        [
            new SalePaymentProofInput(Guid.CreateVersion7(), 30_000m),
            new SalePaymentProofInput(Guid.CreateVersion7(), 20_000m)
        ]);

        Assert.Equal(2, sale.PaymentProofs.Count);
    }
}
