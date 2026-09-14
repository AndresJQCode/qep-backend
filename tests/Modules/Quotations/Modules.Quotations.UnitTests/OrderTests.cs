using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class OrderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly QuotationId QuotationId = Domain.QuotationId.New();
    private static readonly MemberId ConvertedBy = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static Order NewOrder(
        OrderPaymentStatus paymentStatus = OrderPaymentStatus.FullPaymentReceived,
        string? notes = null,
        IReadOnlyCollection<OrderPaymentProofInput>? proofs = null) =>
        Order.Create(
            OrderId.New(),
            TenantId,
            "VEN-2026-0001",
            QuotationId,
            paymentStatus,
            notes,
            ConvertedBy,
            proofs ?? [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)],
            Now);

    [Fact]
    public void CreateStartsPendingWithItsProofs()
    {
        var fileId = Guid.CreateVersion7();
        var order = NewOrder(proofs: [new OrderPaymentProofInput(fileId, 50_000m)]);

        // Nace pendiente: quien la registra no es quien la aprueba.
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Null(order.ApprovedAt);
        Assert.Null(order.ApprovedBy);
        Assert.Equal(QuotationId, order.QuotationId);
        Assert.Equal(ConvertedBy, order.ConvertedBy);
        Assert.Equal(Now, order.ConvertedAt);
        Assert.Null(order.RitualCollectionSyncId);
        Assert.Equal(1, order.Version);
        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(fileId, proof.FileId);
        Assert.Equal(50_000m, proof.Amount);
        Assert.Equal(ConvertedBy, proof.UploadedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankOrderNumber(string number)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.Create(
                OrderId.New(), TenantId, number, QuotationId, OrderPaymentStatus.FullPaymentReceived,
                null, ConvertedBy, [new OrderPaymentProofInput(Guid.CreateVersion7(), 1m)], Now));

        Assert.Equal("order.order.number_required", error.Code);
    }

    // US-14: se requiere al menos un comprobante, salvo que el pago quede pendiente.
    [Fact]
    public void CreateRequiresAtLeastOneProofUnlessPaymentIsPending()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewOrder(paymentStatus: OrderPaymentStatus.PartialPaymentReceived, proofs: []));

        Assert.Equal("order.order.payment_proof_required", error.Code);
    }

    [Fact]
    public void CreateAllowsNoProofsWhenPaymentIsPending()
    {
        var order = NewOrder(paymentStatus: OrderPaymentStatus.PaymentPending, proofs: []);

        Assert.Empty(order.PaymentProofs);
        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
    }

    [Fact]
    public void CreateRejectsAProofWithoutAFile()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewOrder(proofs: [new OrderPaymentProofInput(Guid.Empty, 100m)]));

        Assert.Equal("order.payment_proof.file_required", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateRejectsAProofWithANonPositiveAmount(decimal amount)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), amount)]));

        Assert.Equal("order.payment_proof.amount_invalid", error.Code);
    }

    [Fact]
    public void CreateAcceptsSeveralProofsWithDifferentAmounts()
    {
        var order = NewOrder(proofs:
        [
            new OrderPaymentProofInput(Guid.CreateVersion7(), 30_000m),
            new OrderPaymentProofInput(Guid.CreateVersion7(), 20_000m)
        ]);

        Assert.Equal(2, order.PaymentProofs.Count);
    }

    // A pedido (2026-09): "Aprobar pedido" se bloquea mientras el pago no está completo, y esto
    // es la forma de destrabarlo sin recrear el pedido.
    [Fact]
    public void AddPaymentProofsSumsProofsAndUpdatesThePaymentStatus()
    {
        var order = NewOrder(
            paymentStatus: OrderPaymentStatus.PaymentPending, proofs: []);
        var newFileId = Guid.CreateVersion7();
        var later = Now.AddDays(1);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(newFileId, 80_000m)],
            OrderPaymentStatus.FullPaymentReceived,
            "Pago completado por transferencia",
            ConvertedBy,
            later);

        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(newFileId, proof.FileId);
        Assert.Equal(80_000m, proof.Amount);
        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Equal("Pago completado por transferencia", order.Notes);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // La pantalla precarga la nota con lo que ya había: mandar null (nadie tocó el campo) no
    // tiene por qué borrarla, pero tampoco hay forma de que este método lo sepa -- reemplaza
    // entero, como el resto de sus datos. Se deja explícito para que un cambio futuro no lo
    // convierta sin querer en "conservar si es null".
    [Fact]
    public void AddPaymentProofsReplacesTheNotesEntirelyEvenToNull()
    {
        var order = NewOrder(
            paymentStatus: OrderPaymentStatus.PaymentPending,
            notes: "Nota original",
            proofs: []);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1));

        Assert.Null(order.Notes);
    }

    [Fact]
    public void AddPaymentProofsKeepsProofsAlreadyThere()
    {
        var existingFileId = Guid.CreateVersion7();
        var order = NewOrder(proofs: [new OrderPaymentProofInput(existingFileId, 50_000m)]);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1));

        Assert.Equal(2, order.PaymentProofs.Count);
        Assert.Contains(order.PaymentProofs, proof => proof.FileId == existingFileId);
    }

    [Fact]
    public void AddPaymentProofsRejectsAnEmptyBatch()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [], OrderPaymentStatus.FullPaymentReceived, null, ConvertedBy, Now));

        Assert.Equal("order.order.payment_proof_required", error.Code);
    }

    // Aprobado, el pedido es el respaldo de un cobro que alguien ya revisó: sumarle
    // comprobantes ahí adentro cambiaría lo que esa persona dio por bueno.
    [Fact]
    public void AddPaymentProofsRejectsAnAlreadyApprovedOrder()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)],
                OrderPaymentStatus.FullPaymentReceived,
                null,
                ConvertedBy,
                Now.AddDays(1)));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    // A pedido (2026-09): corregir un monto mal tipeado sin recrear el pedido ni el
    // comprobante -- el archivo y quien lo subio no cambian.
    [Fact]
    public void AddPaymentProofsCorrectsAnExistingProofAmount()
    {
        var existingFileId = Guid.CreateVersion7();
        var order = NewOrder(
            paymentStatus: OrderPaymentStatus.PartialPaymentReceived,
            proofs: [new OrderPaymentProofInput(existingFileId, 50_000m)]);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var later = Now.AddDays(1);

        order.AddPaymentProofs(
            [],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            later,
            [new OrderPaymentProofAmountUpdate(proofId, 80_000m)]);

        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(proofId, proof.Id);
        Assert.Equal(existingFileId, proof.FileId);
        Assert.Equal(80_000m, proof.Amount);
        Assert.Equal(ConvertedBy, proof.UploadedBy);
        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Equal(2, order.Version);
    }

    // Sumar y corregir en el mismo llamado: un solo viaje de red para las dos cosas.
    [Fact]
    public void AddPaymentProofsAddsNewOnesAndCorrectsExistingOnesTogether()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)]);
        var existingProofId = Assert.Single(order.PaymentProofs).Id;
        var newFileId = Guid.CreateVersion7();

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(newFileId, 20_000m)],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(existingProofId, 60_000m)]);

        Assert.Equal(2, order.PaymentProofs.Count);
        Assert.Contains(
            order.PaymentProofs, proof => proof.Id == existingProofId && proof.Amount == 60_000m);
        Assert.Contains(
            order.PaymentProofs, proof => proof.FileId == newFileId && proof.Amount == 20_000m);
    }

    [Fact]
    public void AddPaymentProofsRejectsCorrectingAProofThatIsNotOnThisOrder()
    {
        var order = NewOrder();
        var unknownProofId = OrderPaymentProofId.New();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [],
                order.PaymentStatus,
                null,
                ConvertedBy,
                Now.AddDays(1),
                [new OrderPaymentProofAmountUpdate(unknownProofId, 10_000m)]));

        Assert.Equal("order.payment_proof.not_found", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddPaymentProofsRejectsCorrectingAProofToANonPositiveAmount(decimal amount)
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [],
                order.PaymentStatus,
                null,
                ConvertedBy,
                Now.AddDays(1),
                [new OrderPaymentProofAmountUpdate(proofId, amount)]));

        Assert.Equal("order.payment_proof.amount_invalid", error.Code);
    }

    // Correcciones solas, sin sumar ningun comprobante nuevo, siguen siendo "algo para hacer" --
    // el chequeo de lote vacio no debe pedir tambien un comprobante nuevo.
    [Fact]
    public void AddPaymentProofsAllowsOnlyCorrectingWithoutAddingAnyNewProof()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;

        order.AddPaymentProofs(
            [],
            order.PaymentStatus,
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(proofId, 5_000m)]);

        Assert.Equal(5_000m, Assert.Single(order.PaymentProofs).Amount);
    }

    // Aprobado, el pedido es el respaldo de un cobro que alguien ya reviso con esos montos:
    // corregirlos ahi adentro cambiaria lo que esa persona dio por bueno.
    [Fact]
    public void AddPaymentProofsRejectsCorrectingAProofOnAnAlreadyApprovedOrder()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [],
                order.PaymentStatus,
                null,
                ConvertedBy,
                Now.AddDays(1),
                [new OrderPaymentProofAmountUpdate(proofId, 5_000m)]));

        Assert.Equal("order.order.not_pending", error.Code);
    }
}
