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
        string? notes = null,
        IReadOnlyCollection<SalePaymentProofInput>? proofs = null) =>
        Sale.Create(
            SaleId.New(),
            TenantId,
            "VEN-2026-0001",
            QuotationId,
            paymentStatus,
            notes,
            ConvertedBy,
            proofs ?? [new SalePaymentProofInput(Guid.CreateVersion7(), 100_000m)],
            Now);

    [Fact]
    public void CreateStartsPendingWithItsProofs()
    {
        var fileId = Guid.CreateVersion7();
        var sale = NewSale(proofs: [new SalePaymentProofInput(fileId, 50_000m)]);

        // Nace pendiente: quien la registra no es quien la aprueba.
        Assert.Equal(SaleStatus.Pending, sale.Status);
        Assert.Null(sale.ApprovedAt);
        Assert.Null(sale.ApprovedBy);
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

    // A pedido (2026-09): "Aprobar venta" se bloquea mientras el pago no está completo, y esto
    // es la forma de destrabarlo sin recrear la venta.
    [Fact]
    public void AddPaymentProofsSumsProofsAndUpdatesThePaymentStatus()
    {
        var sale = NewSale(
            paymentStatus: SalePaymentStatus.PaymentPending, proofs: []);
        var newFileId = Guid.CreateVersion7();
        var later = Now.AddDays(1);

        sale.AddPaymentProofs(
            [new SalePaymentProofInput(newFileId, 80_000m)],
            SalePaymentStatus.FullPaymentReceived,
            "Pago completado por transferencia",
            ConvertedBy,
            later);

        var proof = Assert.Single(sale.PaymentProofs);
        Assert.Equal(newFileId, proof.FileId);
        Assert.Equal(80_000m, proof.Amount);
        Assert.Equal(SalePaymentStatus.FullPaymentReceived, sale.PaymentStatus);
        Assert.Equal("Pago completado por transferencia", sale.Notes);
        Assert.Equal(later, sale.UpdatedAt);
        Assert.Equal(2, sale.Version);
    }

    // La pantalla precarga la nota con lo que ya había: mandar null (nadie tocó el campo) no
    // tiene por qué borrarla, pero tampoco hay forma de que este método lo sepa -- reemplaza
    // entero, como el resto de sus datos. Se deja explícito para que un cambio futuro no lo
    // convierta sin querer en "conservar si es null".
    [Fact]
    public void AddPaymentProofsReplacesTheNotesEntirelyEvenToNull()
    {
        var sale = NewSale(
            paymentStatus: SalePaymentStatus.PaymentPending,
            notes: "Nota original",
            proofs: []);

        sale.AddPaymentProofs(
            [new SalePaymentProofInput(Guid.CreateVersion7(), 50_000m)],
            SalePaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1));

        Assert.Null(sale.Notes);
    }

    [Fact]
    public void AddPaymentProofsKeepsProofsAlreadyThere()
    {
        var existingFileId = Guid.CreateVersion7();
        var sale = NewSale(proofs: [new SalePaymentProofInput(existingFileId, 50_000m)]);

        sale.AddPaymentProofs(
            [new SalePaymentProofInput(Guid.CreateVersion7(), 50_000m)],
            SalePaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1));

        Assert.Equal(2, sale.PaymentProofs.Count);
        Assert.Contains(sale.PaymentProofs, proof => proof.FileId == existingFileId);
    }

    [Fact]
    public void AddPaymentProofsRejectsAnEmptyBatch()
    {
        var sale = NewSale();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            sale.AddPaymentProofs(
                [], SalePaymentStatus.FullPaymentReceived, null, ConvertedBy, Now));

        Assert.Equal("sale.sale.payment_proof_required", error.Code);
    }

    // Aprobada, la venta es el respaldo de un cobro que alguien ya revisó: sumarle
    // comprobantes ahí adentro cambiaría lo que esa persona dio por bueno.
    [Fact]
    public void AddPaymentProofsRejectsAnAlreadyApprovedSale()
    {
        var sale = NewSale();
        sale.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            sale.AddPaymentProofs(
                [new SalePaymentProofInput(Guid.CreateVersion7(), 10_000m)],
                SalePaymentStatus.FullPaymentReceived,
                null,
                ConvertedBy,
                Now.AddDays(1)));

        Assert.Equal("sale.sale.not_pending", error.Code);
    }
}
