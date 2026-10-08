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
            "PED-2026-0001",
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

    // A pedido (2026-09-15): reemplazar el archivo de un comprobante mal cargado, junto con la
    // corrección de monto que ya existía.
    [Fact]
    public void AddPaymentProofsReplacesTheFileOfAnExistingProofWhenGiven()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)]);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var newFileId = Guid.CreateVersion7();

        order.AddPaymentProofs(
            [],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(proofId, 80_000m, newFileId)]);

        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(newFileId, proof.FileId);
        Assert.Equal(80_000m, proof.Amount);
    }

    [Fact]
    public void AddPaymentProofsKeepsTheFileWhenNoNewFileIdIsGiven()
    {
        var existingFileId = Guid.CreateVersion7();
        var order = NewOrder(proofs: [new OrderPaymentProofInput(existingFileId, 50_000m)]);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        order.AddPaymentProofs(
            [],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(proofId, 80_000m)]);

        Assert.Equal(existingFileId, Assert.Single(order.PaymentProofs).FileId);
    }

    // A pedido (2026-09-15): quitar un comprobante cargado por error, distinto de corregirlo.
    [Fact]
    public void RemovePaymentProofRemovesAnExistingProof()
    {
        var order = NewOrder(proofs:
        [
            new OrderPaymentProofInput(Guid.CreateVersion7(), 30_000m),
            new OrderPaymentProofInput(Guid.CreateVersion7(), 20_000m),
        ]);
        var proofId = order.PaymentProofs.First().Id;
        var later = Now.AddDays(1);

        order.RemovePaymentProof(proofId, later);

        Assert.Single(order.PaymentProofs);
        Assert.DoesNotContain(order.PaymentProofs, proof => proof.Id == proofId);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    [Fact]
    public void RemovePaymentProofRejectsAnUnknownProof()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.RemovePaymentProof(OrderPaymentProofId.New(), Now.AddDays(1)));

        Assert.Equal("order.payment_proof.not_found", error.Code);
    }

    // Aprobado, el pedido es el respaldo de un cobro que alguien ya revisó con los comprobantes
    // que tenía en ese momento.
    [Fact]
    public void RemovePaymentProofRejectsAnAlreadyApprovedOrder()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.RemovePaymentProof(proofId, Now.AddDays(1)));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    // A pedido (2026-09): agregar un producto desde "Editar" pedido sube el total de la
    // cotización; lo cargado en comprobantes no cambia, pero el estado de pago sí puede.
    [Theory]
    [InlineData(50_000, 100_000, OrderPaymentStatus.PartialPaymentReceived)]
    [InlineData(100_000, 100_000, OrderPaymentStatus.FullPaymentReceived)]
    [InlineData(120_000, 100_000, OrderPaymentStatus.FullPaymentReceived)]
    public void RecalculatePaymentStatusComparesProofsAgainstTheNewTotal(
        decimal proofAmount, decimal amountDue, OrderPaymentStatus expected)
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), proofAmount)]);
        var later = Now.AddDays(1);

        order.RecalculatePaymentStatus(amountDue, later);

        Assert.Equal(expected, order.PaymentStatus);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    [Fact]
    public void RecalculatePaymentStatusIsPendingWithoutAnyProof()
    {
        var order = NewOrder(paymentStatus: OrderPaymentStatus.PaymentPending, proofs: []);

        order.RecalculatePaymentStatus(100_000m, Now.AddDays(1));

        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
    }

    // Aprobado, el pedido es el respaldo de un cobro que alguien ya revisó con el total que
    // tenía en ese momento — este método no se llama en ese caso (el caso de uso que agrega el
    // producto ya lo bloquea antes), pero el agregado se defiende igual.
    [Fact]
    public void RecalculatePaymentStatusRejectsAnAlreadyApprovedOrder()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.RecalculatePaymentStatus(200_000m, Now.AddDays(1)));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    // Spec 2026-09-15, P5: cada comprobante guarda la clave de su copia pública, que le llega ya
    // armada desde el handler.
    [Fact]
    public void CreateKeepsThePublicStorageKeyOfEachProof()
    {
        var order = NewOrder(proofs:
        [
            new OrderPaymentProofInput(Guid.CreateVersion7(), 60_000m, "payment-proofs/a.pdf"),
            new OrderPaymentProofInput(Guid.CreateVersion7(), 40_000m, "payment-proofs/b.png"),
        ]);

        Assert.Equal(
            ["payment-proofs/a.pdf", "payment-proofs/b.png"],
            order.PaymentProofs.Select(proof => proof.PublicStorageKey));
    }

    // P8: con la opción apagada, y en los comprobantes de antes, no hay copia pública.
    [Fact]
    public void CreateLeavesThePublicStorageKeyNullWhenThereIsNone()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);

        Assert.Null(Assert.Single(order.PaymentProofs).PublicStorageKey);
    }

    [Fact]
    public void AddPaymentProofsKeepsThePublicStorageKeyOfTheNewProofs()
    {
        var order = NewOrder(paymentStatus: OrderPaymentStatus.PaymentPending, proofs: []);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m, "payment-proofs/c.jpg")],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1));

        Assert.Equal("payment-proofs/c.jpg", Assert.Single(order.PaymentProofs).PublicStorageKey);
    }

    // P4: corregir el monto no toca el archivo, así que tampoco su copia pública.
    [Fact]
    public void CorrectingAnAmountKeepsThePublicStorageKey()
    {
        var order = NewOrder(
            paymentStatus: OrderPaymentStatus.PartialPaymentReceived,
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m, "payment-proofs/d.pdf")]);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        order.AddPaymentProofs(
            [],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(proofId, 80_000m)]);

        Assert.Equal("payment-proofs/d.pdf", Assert.Single(order.PaymentProofs).PublicStorageKey);
    }

    // Spec 2026-09-16 (anular un pedido), decisiones 1 y 2: se anula desde Pending, y queda
    // quién, cuándo y por qué.
    [Fact]
    public void CancelFromPendingRecordsWhoWhenAndWhy()
    {
        var order = NewOrder();
        Assert.Null(order.CancelledAt);
        Assert.Null(order.CancelledBy);
        Assert.Null(order.CancellationReason);
        var cancelledBy = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(1);

        order.Cancel(cancelledBy, "El cliente desistió", later);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(later, order.CancelledAt);
        Assert.Equal(cancelledBy, order.CancelledBy);
        Assert.Equal("El cliente desistió", order.CancellationReason);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // Decisiones 1 y 2: un pedido aprobado por error también se anula, y anularlo no reescribe
    // quién lo revisó ni cuándo.
    [Fact]
    public void CancelFromApprovedKeepsTheApproval()
    {
        var order = NewOrder();
        var approvedBy = new MemberId(Guid.CreateVersion7());
        order.Approve(approvedBy, Now);

        order.Cancel(ConvertedBy, "Aprobado por error", Now.AddDays(1));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(approvedBy, order.ApprovedBy);
        Assert.Equal(Now, order.ApprovedAt);
        Assert.Equal(3, order.Version);
    }

    // Anular dos veces reescribiría quién, cuándo y por qué se anuló.
    [Fact]
    public void CancelTwiceIsRejectedAndKeepsTheFirstCancellation()
    {
        var order = NewOrder();
        order.Cancel(ConvertedBy, "Primera vez", Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(new MemberId(Guid.CreateVersion7()), "Segunda vez", Now.AddDays(1)));

        Assert.Equal("order.order.already_cancelled", error.Code);
        Assert.Equal("Primera vez", order.CancellationReason);
        Assert.Equal(Now, order.CancelledAt);
        Assert.Equal(ConvertedBy, order.CancelledBy);
    }

    // Decisión 4: el motivo es obligatorio. Ya anulado gana already_cancelled (hallazgo 6).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CancelRequiresAReason(string? reason)
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(ConvertedBy, reason, Now));

        Assert.Equal("order.order.cancellation_reason_required", error.Code);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Null(order.CancelledAt);
    }

    [Fact]
    public void CancelRejectsAReasonLongerThanTheLimit()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(ConvertedBy, new string('a', Order.CancellationReasonMaxLength + 1), Now));

        Assert.Equal("order.order.cancellation_reason_too_long", error.Code);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    // El límite se mide sobre el motivo recortado, mismo criterio que las notas.
    [Fact]
    public void CancelTrimsTheReasonBeforeMeasuringIt()
    {
        var order = NewOrder();
        var reason = new string('a', Order.CancellationReasonMaxLength);

        order.Cancel(ConvertedBy, $"  {reason}  ", Now);

        Assert.Equal(reason, order.CancellationReason);
    }

    // Los guards existentes ya cubren un anulado: no es Pending, así que nada de lo que sólo se
    // permite en Pending pasa (spec, «Dominio»).
    [Fact]
    public void ACancelledOrderRejectsEveryPendingOnlyChange()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.Cancel(ConvertedBy, "El cliente desistió", Now);
        var later = Now.AddDays(1);

        var addProofs = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)],
                OrderPaymentStatus.FullPaymentReceived,
                null,
                ConvertedBy,
                later));
        var recalculate = Assert.Throws<QuotationsDomainException>(() =>
            order.RecalculatePaymentStatus(200_000m, later));
        var removeProof = Assert.Throws<QuotationsDomainException>(() =>
            order.RemovePaymentProof(proofId, later));
        var approve = Assert.Throws<QuotationsDomainException>(() =>
            order.Approve(ConvertedBy, later));

        Assert.Equal("order.order.not_pending", addProofs.Code);
        Assert.Equal("order.order.not_pending", recalculate.Code);
        Assert.Equal("order.order.not_pending", removeProof.Code);
        Assert.Equal("order.order.not_pending", approve.Code);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    // Spec 2026-09-17 (editar pedido como borrador): el guardado atómico suma comprobantes sin
    // pisar el estado de pago —lo deriva el servidor una sola vez, decisión 5— ni las notas.
    [Fact]
    public void AttachPaymentProofsAddsTheProofsWithoutTouchingPaymentStatusOrNotes()
    {
        var order = NewOrder(paymentStatus: OrderPaymentStatus.PartialPaymentReceived, notes: "Entregar el lunes");
        var fileId = Guid.CreateVersion7();
        var uploadedBy = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(1);

        order.AttachPaymentProofs([new OrderPaymentProofInput(fileId, 30_000m, "payment-proofs/a.pdf")], uploadedBy, later);

        Assert.Equal(2, order.PaymentProofs.Count);
        var added = Assert.Single(order.PaymentProofs, proof => proof.FileId == fileId);
        Assert.Equal(30_000m, added.Amount);
        Assert.Equal(uploadedBy, added.UploadedBy);
        Assert.Equal("payment-proofs/a.pdf", added.PublicStorageKey);
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);
        Assert.Equal("Entregar el lunes", order.Notes);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // A diferencia de AddPaymentProofs, un guardado que sólo toca productos no trae comprobantes:
    // no es un error y no sube la versión.
    [Fact]
    public void AttachPaymentProofsWithNothingToAttachChangesNothing()
    {
        var order = NewOrder();

        order.AttachPaymentProofs([], ConvertedBy, Now.AddDays(1));

        Assert.Single(order.PaymentProofs);
        Assert.Equal(Now, order.UpdatedAt);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void AttachPaymentProofsRejectsAnOrderThatIsNotPending()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachPaymentProofs([], ConvertedBy, Now.AddDays(1)));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    [Fact]
    public void CorrectPaymentProofsUpdatesTheAmountAndReplacesTheFile()
    {
        var order = NewOrder();
        var proof = Assert.Single(order.PaymentProofs);
        var newFileId = Guid.CreateVersion7();
        var later = Now.AddDays(1);

        order.CorrectPaymentProofs(
            [new OrderPaymentProofAmountUpdate(proof.Id, 80_000m, newFileId, "payment-proofs/b.pdf")],
            later);

        Assert.Equal(80_000m, proof.Amount);
        Assert.Equal(newFileId, proof.FileId);
        Assert.Equal("payment-proofs/b.pdf", proof.PublicStorageKey);
        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // Todos los ids se buscan antes de corregir el primero: uno ajeno no deja la mitad corregida.
    [Fact]
    public void CorrectPaymentProofsRejectsAProofThatIsNotOnThisOrderBeforeCorrectingAny()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);
        var proof = Assert.Single(order.PaymentProofs);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectPaymentProofs(
                [
                    new OrderPaymentProofAmountUpdate(proof.Id, 1m),
                    new OrderPaymentProofAmountUpdate(OrderPaymentProofId.New(), 2m),
                ],
                Now.AddDays(1)));

        Assert.Equal("order.payment_proof.not_found", error.Code);
        Assert.Equal(100_000m, proof.Amount);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void CorrectPaymentProofsWithNothingToCorrectChangesNothing()
    {
        var order = NewOrder();

        order.CorrectPaymentProofs([], Now.AddDays(1));

        Assert.Equal(1, order.Version);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Fact]
    public void CorrectPaymentProofsRejectsAnOrderThatIsNotPending()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectPaymentProofs([], Now.AddDays(1)));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    // Spec 2026-09-17: notes reemplaza el campo entero, y el guardado necesita saber si cambió para
    // responder «sin cambios» sin subir la versión.
    [Fact]
    public void UpdateNotesReplacesTheTrimmedNotesAndReportsTheChange()
    {
        var order = NewOrder(notes: "Entregar el lunes");
        var later = Now.AddDays(1);

        var changed = order.UpdateNotes("  Entregar el martes  ", later);

        Assert.True(changed);
        Assert.Equal("Entregar el martes", order.Notes);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    [Fact]
    public void UpdateNotesWithTheSameNotesReportsNoChange()
    {
        var order = NewOrder(notes: "Entregar el lunes");

        var changed = order.UpdateNotes(" Entregar el lunes ", Now.AddDays(1));

        Assert.False(changed);
        Assert.Equal(1, order.Version);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void UpdateNotesClearsThemWithNullOrBlank(string? notes)
    {
        var order = NewOrder(notes: "Entregar el lunes");

        var changed = order.UpdateNotes(notes, Now.AddDays(1));

        Assert.True(changed);
        Assert.Null(order.Notes);
    }

    [Fact]
    public void UpdateNotesRejectsNotesLongerThanTheLimit()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.UpdateNotes(new string('a', Order.NotesMaxLength + 1), Now.AddDays(1)));

        Assert.Equal("order.order.notes_too_long", error.Code);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void UpdateNotesRejectsAnOrderThatIsNotPending()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.UpdateNotes("Otra nota", Now.AddDays(1)));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    // Spec 2026-10-05 (facturar un pedido), decisiones 1 y 3: se factura un aprobado, queda quién
    // y cuándo, y la aprobación no se toca.
    [Fact]
    public void InvoiceAnApprovedOrderRecordsWhoAndWhenAndKeepsTheApproval()
    {
        var order = NewOrder();
        Assert.Null(order.InvoicedAt);
        Assert.Null(order.InvoicedBy);
        var approvedBy = new MemberId(Guid.CreateVersion7());
        order.Approve(approvedBy, Now);
        var invoicedBy = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(1);

        order.Invoice(invoicedBy, later);

        Assert.Equal(OrderStatus.Invoiced, order.Status);
        Assert.Equal(later, order.InvoicedAt);
        Assert.Equal(invoicedBy, order.InvoicedBy);
        Assert.Equal(approvedBy, order.ApprovedBy);
        Assert.Equal(Now, order.ApprovedAt);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(3, order.Version);
    }

    // Decisión 6: de momento facturar no mira el pago, igual que aprobar.
    [Fact]
    public void InvoiceDoesNotRequireTheOrderToBePaid()
    {
        var order = NewOrder(paymentStatus: OrderPaymentStatus.PaymentPending, proofs: []);
        order.Approve(ConvertedBy, Now);

        order.Invoice(ConvertedBy, Now.AddDays(1));

        Assert.Equal(OrderStatus.Invoiced, order.Status);
        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
    }

    // Decisión 1: sólo desde Approved. Desde Pending se saltaría la revisión; un anulado ya no es
    // una venta.
    [Fact]
    public void InvoiceRejectsAPendingOrACancelledOrder()
    {
        var pending = NewOrder();
        var cancelled = NewOrder();
        cancelled.Cancel(ConvertedBy, "El cliente desistió", Now);

        var fromPending = Assert.Throws<QuotationsDomainException>(() =>
            pending.Invoice(ConvertedBy, Now));
        var fromCancelled = Assert.Throws<QuotationsDomainException>(() =>
            cancelled.Invoice(ConvertedBy, Now));

        Assert.Equal("order.order.not_approved", fromPending.Code);
        Assert.Equal("order.order.not_approved", fromCancelled.Code);
        Assert.Equal(OrderStatus.Pending, pending.Status);
        Assert.Null(pending.InvoicedAt);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.InvoicedBy);
    }

    // Facturar dos veces reescribiría quién y cuándo se facturó.
    [Fact]
    public void InvoiceTwiceIsRejectedAndKeepsTheFirstInvoicing()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);
        order.Invoice(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Invoice(new MemberId(Guid.CreateVersion7()), Now.AddDays(1)));

        Assert.Equal("order.order.not_approved", error.Code);
        Assert.Equal(ConvertedBy, order.InvoicedBy);
        Assert.Equal(Now, order.InvoicedAt);
        Assert.Equal(3, order.Version);
    }

    // Decisión 4: revertir vuelve a Approved y limpia las marcas. La aprobación queda.
    [Fact]
    public void RevertInvoicingReturnsToApprovedWithoutTheMarks()
    {
        var order = NewOrder();
        var approvedBy = new MemberId(Guid.CreateVersion7());
        order.Approve(approvedBy, Now);
        order.Invoice(ConvertedBy, Now.AddDays(1));
        var later = Now.AddDays(2);

        order.RevertInvoicing(new MemberId(Guid.CreateVersion7()), later);

        Assert.Equal(OrderStatus.Approved, order.Status);
        Assert.Null(order.InvoicedAt);
        Assert.Null(order.InvoicedBy);
        Assert.Equal(approvedBy, order.ApprovedBy);
        Assert.Equal(Now, order.ApprovedAt);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(4, order.Version);
    }

    // Review Focus 3: el pedido revertido es un Approved limpio. Se vuelve a facturar con el quién y
    // el cuándo nuevos, o se anula.
    [Fact]
    public void ARevertedOrderCanBeInvoicedAgainOrCancelled()
    {
        var reinvoiced = NewOrder();
        reinvoiced.Approve(ConvertedBy, Now);
        reinvoiced.Invoice(ConvertedBy, Now);
        reinvoiced.RevertInvoicing(ConvertedBy, Now.AddDays(1));
        var secondBiller = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(2);

        reinvoiced.Invoice(secondBiller, later);

        Assert.Equal(OrderStatus.Invoiced, reinvoiced.Status);
        Assert.Equal(secondBiller, reinvoiced.InvoicedBy);
        Assert.Equal(later, reinvoiced.InvoicedAt);

        var cancelled = NewOrder();
        cancelled.Approve(ConvertedBy, Now);
        cancelled.Invoice(ConvertedBy, Now);
        cancelled.RevertInvoicing(ConvertedBy, Now.AddDays(1));

        cancelled.Cancel(ConvertedBy, "Facturado por error", later);

        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.InvoicedAt);
    }

    [Fact]
    public void RevertInvoicingRejectsAnOrderThatIsNotInvoiced()
    {
        var pending = NewOrder();
        var approved = NewOrder();
        approved.Approve(ConvertedBy, Now);
        var cancelled = NewOrder();
        cancelled.Cancel(ConvertedBy, "El cliente desistió", Now);

        var fromPending = Assert.Throws<QuotationsDomainException>(() =>
            pending.RevertInvoicing(ConvertedBy, Now.AddDays(1)));
        var fromApproved = Assert.Throws<QuotationsDomainException>(() =>
            approved.RevertInvoicing(ConvertedBy, Now.AddDays(1)));
        var fromCancelled = Assert.Throws<QuotationsDomainException>(() =>
            cancelled.RevertInvoicing(ConvertedBy, Now.AddDays(1)));

        Assert.Equal("order.order.not_invoiced", fromPending.Code);
        Assert.Equal("order.order.not_invoiced", fromApproved.Code);
        Assert.Equal("order.order.not_invoiced", fromCancelled.Code);
        Assert.Equal(OrderStatus.Approved, approved.Status);
        Assert.Equal(2, approved.Version);
    }

    // Decisión 2: un facturado no se anula. El guard va antes que already_cancelled y que las reglas
    // del motivo (spec, «Dominio»): con el motivo vacío la respuesta sigue siendo already_invoiced.
    [Theory]
    [InlineData("El cliente desistió")]
    [InlineData(null)]
    [InlineData("   ")]
    public void CancelAnInvoicedOrderIsRejectedBeforeLookingAtTheReason(string? reason)
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);
        order.Invoice(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(ConvertedBy, reason, Now.AddDays(1)));

        Assert.Equal("order.order.already_invoiced", error.Code);
        Assert.Equal(OrderStatus.Invoiced, order.Status);
        Assert.Null(order.CancelledAt);
        Assert.Null(order.CancellationReason);
    }

    // Los guards existentes ya cubren un facturado: no es Pending, así que nada de lo que sólo se
    // permite en Pending pasa (spec, «Dominio»). Tampoco se vuelve a aprobar.
    [Fact]
    public void AnInvoicedOrderRejectsEveryPendingOnlyChange()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.Approve(ConvertedBy, Now);
        order.Invoice(ConvertedBy, Now);
        var later = Now.AddDays(1);

        var addProofs = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)],
                OrderPaymentStatus.FullPaymentReceived,
                null,
                ConvertedBy,
                later));
        var recalculate = Assert.Throws<QuotationsDomainException>(() =>
            order.RecalculatePaymentStatus(200_000m, later));
        var removeProof = Assert.Throws<QuotationsDomainException>(() =>
            order.RemovePaymentProof(proofId, later));
        var approve = Assert.Throws<QuotationsDomainException>(() =>
            order.Approve(ConvertedBy, later));
        var attach = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachPaymentProofs([], ConvertedBy, later));
        var correct = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectPaymentProofs([], later));
        var notes = Assert.Throws<QuotationsDomainException>(() =>
            order.UpdateNotes("Otra nota", later));

        Assert.Equal("order.order.not_pending", addProofs.Code);
        Assert.Equal("order.order.not_pending", recalculate.Code);
        Assert.Equal("order.order.not_pending", removeProof.Code);
        Assert.Equal("order.order.not_pending", approve.Code);
        Assert.Equal("order.order.not_pending", attach.Code);
        Assert.Equal("order.order.not_pending", correct.Code);
        Assert.Equal("order.order.not_pending", notes.Code);
        Assert.Equal(OrderStatus.Invoiced, order.Status);
        Assert.Equal(3, order.Version);
    }

    // A pedido (2026-10-08): la fecha del comprobante la escribe quien lo adjunta —es la del
    // soporte de pago, no la del día en que se subió— y viaja con él desde que nace.
    [Fact]
    public void CreateKeepsThePaidOnDateOfEachProof()
    {
        var paidOn = new DateOnly(2026, 10, 6);

        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m, PaidOn: paidOn)]);

        Assert.Equal(paidOn, Assert.Single(order.PaymentProofs).PaidOn);
    }

    // Los comprobantes anteriores al campo no tienen fecha: null, no un valor inventado.
    [Fact]
    public void AProofWithoutPaidOnStaysWithoutADate()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)]);

        Assert.Null(Assert.Single(order.PaymentProofs).PaidOn);
    }

    [Fact]
    public void AddPaymentProofsKeepsThePaidOnDateOfTheNewProof()
    {
        var order = NewOrder(paymentStatus: OrderPaymentStatus.PartialPaymentReceived);
        var fileId = Guid.CreateVersion7();
        var paidOn = new DateOnly(2026, 10, 7);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(fileId, 30_000m, PaidOn: paidOn)],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            new MemberId(Guid.CreateVersion7()),
            Now.AddDays(1));

        var added = Assert.Single(order.PaymentProofs, proof => proof.FileId == fileId);
        Assert.Equal(paidOn, added.PaidOn);
    }

    [Fact]
    public void AttachPaymentProofsKeepsThePaidOnDateOfTheNewProof()
    {
        var order = NewOrder();
        var fileId = Guid.CreateVersion7();
        var paidOn = new DateOnly(2026, 10, 7);

        order.AttachPaymentProofs(
            [new OrderPaymentProofInput(fileId, 30_000m, PaidOn: paidOn)], new MemberId(Guid.CreateVersion7()), Now.AddDays(1));

        var added = Assert.Single(order.PaymentProofs, proof => proof.FileId == fileId);
        Assert.Equal(paidOn, added.PaidOn);
    }

    // A pedido (2026-10-08): al corregir un comprobante, la fecha del soporte se corrige con el
    // monto. La pantalla precarga las dos y manda las dos, así que el valor que viene reemplaza
    // al guardado —null la borra— igual que el monto, y no como `NewFileId`, que null conserva.
    [Fact]
    public void CorrectPaymentProofsUpdatesThePaidOnDate()
    {
        var order = NewOrder(
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m, PaidOn: new DateOnly(2026, 10, 1))]);
        var proof = Assert.Single(order.PaymentProofs);
        var paidOn = new DateOnly(2026, 10, 7);

        order.CorrectPaymentProofs(
            [new OrderPaymentProofAmountUpdate(proof.Id, 100_000m, PaidOn: paidOn)], Now.AddDays(1));

        Assert.Equal(paidOn, proof.PaidOn);
    }

    [Fact]
    public void CorrectPaymentProofsWithoutPaidOnClearsTheDate()
    {
        var order = NewOrder(
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m, PaidOn: new DateOnly(2026, 10, 1))]);
        var proof = Assert.Single(order.PaymentProofs);

        order.CorrectPaymentProofs([new OrderPaymentProofAmountUpdate(proof.Id, 100_000m)], Now.AddDays(1));

        Assert.Null(proof.PaidOn);
    }

    [Fact]
    public void AddPaymentProofsUpdatesThePaidOnDateOfAnExistingProof()
    {
        var order = NewOrder(
            paymentStatus: OrderPaymentStatus.PartialPaymentReceived,
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)]);
        var existingProofId = Assert.Single(order.PaymentProofs).Id;
        var paidOn = new DateOnly(2026, 10, 7);

        order.AddPaymentProofs(
            [],
            OrderPaymentStatus.PartialPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(existingProofId, 60_000m, PaidOn: paidOn)]);

        var corrected = Assert.Single(order.PaymentProofs);
        Assert.Equal(60_000m, corrected.Amount);
        Assert.Equal(paidOn, corrected.PaidOn);
    }
}
