using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Guarda de una vez todo lo que «Editar pedido» cambió en su borrador —productos, comprobantes y
/// notas— (spec 2026-09-17, decisiones 1 y 2). <paramref name="ExpectedVersion"/> es el
/// <c>If-Match</c>: el <c>Order.Version</c> que la pantalla cargó (decisión 6).
/// </summary>
public sealed record SaveOrderEditsCommand(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    IReadOnlyList<OrderItemAddition> Items,
    OrderEditProofs Proofs,
    string? Notes) : ICommand<OrderDetailDto>, IOrderEdits;

public sealed class SaveOrderEditsValidator : OrderEditsValidator<SaveOrderEditsCommand>
{
    public SaveOrderEditsValidator()
        : base(requireFileIds: true)
    {
    }
}

/// <summary>
/// Pasos 1 a 9 del spec 2026-09-17: todo sobre los agregados rastreados y un único
/// <c>SaveChangesAsync</c>, así que una falla en cualquier paso no deja nada a medio guardar.
/// Los endpoints hermanos (<c>/orders/{orderId}/items</c>, <c>/orders/{orderId}/proofs</c>,
/// <c>/quotations/{id}/items/{itemId}</c>) no se tocan
/// (decisión 8).
/// </summary>
public sealed class SaveOrderEditsHandler(
    IQuotationRepository quotationRepository,
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationProductLookup productLookup,
    IQuotationCustomerLookup customerLookup,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SaveOrderEditsCommand> validator)
    : ICommandHandler<SaveOrderEditsCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        SaveOrderEditsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Se entra por el id del pedido —la pantalla viene del listado de pedidos— y recién desde
        // él se llega a su cotización, igual que GetOrderByIdHandler. Que falte la cotización sería
        // un pedido huérfano, imposible por la FK: se trata como el mismo "no encontrado".
        var order = await orderRepository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);
        var quotation = await quotationRepository.FindAsync(
            command.TenantId, order.QuotationId, cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);

        if (order.Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "An order can only be edited while it is pending.");
        }

        // Decisión 6: con guardado de estado completo, «el último que guarda gana» pisaría en
        // silencio lo que otra persona cambió después de que esta pantalla cargó.
        if (order.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The order changed after it was loaded.");
        }

        // Mismo motivo que UpdateQuotationItemHandler: retención/excedente al día antes de recalcular.
        var customer = await customerLookup.FindAsync(command.TenantId, quotation.ClientId, cancellationToken);
        if (customer is not null)
        {
            quotation.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
        }

        // Los archivos se validan contra Storage antes de tocar nada, igual que AddOrderPaymentProofsHandler.
        foreach (var addition in command.Proofs.Add)
        {
            await OrderPaymentProofResolver.ResolveAsync(
                fileLookup, orderRepository, command.TenantId, addition.FileId!.Value, exceptProofId: null,
                cancellationToken);
        }

        foreach (var update in command.Proofs.Update)
        {
            if (update.NewFileId is { } newFileId)
            {
                await OrderPaymentProofResolver.ResolveAsync(
                    fileLookup, orderRepository, command.TenantId, newFileId,
                    new OrderPaymentProofId(update.ProofId), cancellationToken);
            }
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        var removedProofIds = command.Proofs.RemoveIds.Select(id => new OrderPaymentProofId(id)).ToArray();
        var replacedProofIds = command.Proofs.Update
            .Where(update => update.NewFileId is not null)
            .Select(update => new OrderPaymentProofId(update.ProofId))
            .ToHashSet();
        // D19 (spec 2026-09-16): archivo y clave de lo que se quita o reemplaza, leídos ANTES de
        // mutar, porque RemovePaymentProof y UpdateFile los pierden.
        var releaseCandidates = order.PaymentProofs
            .Where(proof => removedProofIds.Contains(proof.Id) || replacedProofIds.Contains(proof.Id))
            .Select(proof => new DetachedPaymentProof(proof.FileId, proof.PublicStorageKey))
            .ToArray();

        var copies = new PaymentProofCopies(paymentProofPublisher);
        try
        {
            var itemEdits = await OrderItemEdits.ApplyAsync(
                quotation, command.Items, pricingLookup, command.TenantId, updatedBy, now, cancellationToken);

            foreach (var proofId in removedProofIds)
            {
                order.RemovePaymentProof(proofId, now);
            }

            var corrections = new List<OrderPaymentProofAmountUpdate>(command.Proofs.Update.Count);
            foreach (var update in command.Proofs.Update)
            {
                string? newPublicStorageKey = null;
                if (update.NewFileId is { } newFileId)
                {
                    newPublicStorageKey = await copies.PublishReplacementAsync(
                        command.TenantId, newFileId, cancellationToken);
                }

                corrections.Add(new OrderPaymentProofAmountUpdate(
                    new OrderPaymentProofId(update.ProofId), update.Amount, update.NewFileId, newPublicStorageKey));
            }

            order.CorrectPaymentProofs(corrections, now);

            var attachedInputs = await copies.PublishAsync(
                command.TenantId,
                command.Proofs.Add
                    .Select(addition => new OrderPaymentProofRequest(addition.FileId!.Value, addition.Amount))
                    .ToArray(),
                cancellationToken);
            order.AttachPaymentProofs(attachedInputs, updatedBy, now);

            var notesChanged = order.UpdateNotes(command.Notes, now);
            var proofsChanged = removedProofIds.Length > 0 || corrections.Count > 0 || attachedInputs.Length > 0;

            // Paso 9: sin cambio real no hay historial, auditoría, recálculo ni escritura, y la
            // versión queda igual (RecalculatePaymentStatus siempre la sube).
            if (itemEdits.Count == 0 && !proofsChanged && !notesChanged)
            {
                return new OrderDetailDto(order.ToDto(), quotation.ToDto());
            }

            await RecordItemEditsAsync(command.TenantId, quotation, order, itemEdits, updatedBy, now, cancellationToken);

            for (var index = 0; index < removedProofIds.Length; index++)
            {
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.payment_proof_removed",
                    order.Id.ToString(), "success", now);
            }

            if (corrections.Count > 0 || attachedInputs.Length > 0)
            {
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.payment_proofs_added",
                    order.Id.ToString(), "success", now);
            }

            var attached = PaymentProofCopies.AttachedFrom(attachedInputs)
                .Concat(PaymentProofCopies.AttachedFromReplacements(corrections))
                .ToArray();
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            var detached = PaymentProofCopies.DetachedFrom(
                releaseCandidates,
                order.PaymentProofs.Select(proof => new DetachedPaymentProof(proof.FileId, proof.PublicStorageKey)));
            if (detached.Length > 0)
            {
                paymentProofEvents.PublishDetached(command.TenantId, order.Id, detached, now);
            }

            // Antes del estado de pago: las altas, bajas y cambios de cantidad de esta tanda
            // mueven el descuento de toda la cotización, y con él su total.
            await QuotationPricingRecalculation.ApplyAsync(
                pricingLookup, command.TenantId, quotation, now, cancellationToken);

            // Pasos 7 y 8: el estado de pago lo deriva el servidor una sola vez (decisión 5), y todo
            // se escribe junto.
            order.RecalculatePaymentStatus(quotation.Total, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await copies.RollbackAsync();
            throw;
        }

        return new OrderDetailDto(order.ToDto(), quotation.ToDto());
    }

    // Historial Edited y auditoría quotation.order.item_* por cada cambio, como los endpoints de hoy.
    // El nombre de una baja se busca en una sola ida al catálogo, igual que BatchUpdateQuotationItemsHandler.
    private async Task RecordItemEditsAsync(
        Guid tenantId,
        Quotation quotation,
        Order order,
        IReadOnlyList<OrderItemEdit> edits,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var removedProductIds = edits
            .Where(edit => edit.Kind == OrderItemEditKind.Removed)
            .Select(edit => edit.ProductId)
            .ToArray();
        var removedProducts = removedProductIds.Length == 0
            ? new Dictionary<Guid, QuotationProductRef>()
            : await productLookup.FindManyAsync(tenantId, removedProductIds, cancellationToken);

        foreach (var edit in edits)
        {
            string summary;
            string action;
            if (edit.Kind == OrderItemEditKind.Added)
            {
                summary = QuotationChangeSummary.ItemAdded(edit.ProductName!, edit.Quantity!.Value);
                action = "quotation.order.item_added";
            }
            else if (edit.Kind == OrderItemEditKind.QuantityChanged)
            {
                summary = QuotationChangeSummary.ItemQuantityChanged(
                    edit.ProductName!, edit.PreviousQuantity!.Value, edit.Quantity!.Value);
                action = "quotation.order.item_updated";
            }
            else
            {
                var productName = removedProducts.TryGetValue(edit.ProductId, out var product)
                    ? product.Name
                    : "un producto";
                summary = QuotationChangeSummary.ItemRemoved(productName);
                action = "quotation.order.item_removed";
            }

            quotationRepository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(), quotation.Id, QuotationHistoryEventType.Edited,
                updatedBy, summary, now));
            // Como en las demás acciones quotation.order.*, la entidad auditada es el pedido.
            auditPublisher.Publish(
                tenantId, executionContext.SubjectId, action, order.Id.ToString(), "success", now);
        }
    }
}
