using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El guardado atómico de «Editar» pedido (spec 2026-09-17): líneas, comprobantes, notas, detal y
/// piso de escala en una sola escritura con <c>If-Match</c>. Desde el spec 2026-10-15 §4.5 todo cae
/// sobre el pedido: la cotización de origen no se toca.
/// </summary>
public sealed record SaveOrderEditsCommand(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    IReadOnlyList<OrderItemAddition> Items,
    OrderEditProofs Proofs,
    string? Notes,
    int? GlobalScaleFloor,
    bool IsRetail,
    ShippingRequest? Shipping = null) : ICommand<OrderDetailDto>, IOrderEdits;

public sealed class SaveOrderEditsValidator : OrderEditsValidator<SaveOrderEditsCommand>
{
    public SaveOrderEditsValidator()
        : base(requireFileIds: true)
    {
    }
}

public sealed class SaveOrderEditsHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    IQuotationProductLookup productLookup,
    IQuotationCustomerLookup customerLookup,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    ShippingResolver shippingResolver,
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

        var order = await orderRepository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);
        if (order.Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "An order can only be edited while it is pending.");
        }

        // El If-Match se comprueba contra lo cargado, antes de tocar nada: un 412 no deja ni la
        // copia pública de un comprobante publicada.
        if (order.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The order changed after it was loaded.");
        }

        // Deja la retención/excedente de IVA al día con el cliente maestro antes de recalcular.
        var customer = await customerLookup.FindAsync(command.TenantId, order.ClientId, cancellationToken);
        if (customer is not null)
        {
            order.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
        }

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
        // Lo que se retira o reemplaza puede dejar de estar publicado, salvo que otro comprobante
        // del pedido siga usando el mismo archivo; se decide al final, con la lista ya editada.
        var releaseCandidates = order.PaymentProofs
            .Where(proof => removedProofIds.Contains(proof.Id) || replacedProofIds.Contains(proof.Id))
            .Select(proof => new DetachedPaymentProof(proof.FileId, proof.PublicStorageKey))
            .ToArray();

        var copies = new PaymentProofCopies(paymentProofPublisher);
        try
        {
            var itemEdits = await OrderItemEdits.ApplyAsync(
                order, command.Items, pricingLookup, command.TenantId, updatedBy, now, command.IsRetail, cancellationToken);

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
                    new OrderPaymentProofId(update.ProofId), update.Amount, update.NewFileId, newPublicStorageKey,
                    update.PaidOn));
            }

            order.CorrectPaymentProofs(corrections, now);
            var attachedInputs = await copies.PublishAsync(
                command.TenantId,
                command.Proofs.Add
                    .Select(addition => new OrderPaymentProofRequest(addition.FileId!.Value, addition.Amount, addition.PaidOn))
                    .ToArray(),
                cancellationToken);
            order.AttachPaymentProofs(attachedInputs, updatedBy, now);

            var notesChanged = order.UpdateNotes(command.Notes, now);
            var retailChanged = order.IsRetail != command.IsRetail;
            if (retailChanged)
            {
                order.SetIsRetail(command.IsRetail, updatedBy, now);
            }

            var floorChanged = order.GlobalScaleFloor != command.GlobalScaleFloor;
            if (floorChanged)
            {
                order.SetGlobalScaleFloor(command.GlobalScaleFloor, updatedBy, now);
            }

            // El envío con la moneda y la recogida del pedido, que no cambian después de creado (P5). Antes de
            // RecalculatePaymentStatus, más abajo: un envío cobrado sube el neto que los comprobantes cubren.
            var shippingBefore = order.ShippingDetails;
            var shipping = await shippingResolver.ResolveAsync(
                command.TenantId, command.Shipping, shippingBefore, order.IsStorePickup, order.Currency,
                "order.order", cancellationToken);
            var shippingChanged = shipping is not null && shipping != shippingBefore;
            string? shippingSummary = null;
            if (shippingChanged)
            {
                order.SetShipping(shipping!, updatedBy, now);
                // Contra lo que quedó en el agregado (ya validado), no contra lo que trajo el resolver.
                shippingSummary = OrderChangeSummary.ShippingChanged(shippingBefore, order.ShippingDetails);
            }

            var proofsChanged = removedProofIds.Length > 0 || corrections.Count > 0 || attachedInputs.Length > 0;
            if (itemEdits.Count == 0 && !proofsChanged && !notesChanged && !floorChanged && !retailChanged
                && !shippingChanged)
            {
                // Nada cambió: ni versión nueva ni historial. El cliente recibe lo que ya tenía.
                return await OrderDetails.LoadAsync(
                    orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
            }

            await RecordItemEditsAsync(command.TenantId, order, itemEdits, updatedBy, now, cancellationToken);
            if (itemEdits.Any(edit => edit.Kind == OrderItemEditKind.Added))
            {
                order.CaptureProductSnapshots(
                    await OrderItemProductLabel.ResolveMissingAsync(productLookup, command.TenantId, order, cancellationToken));
            }

            if (retailChanged)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PricingChanged, updatedBy,
                    OrderChangeSummary.RetailChanged(command.IsRetail), now));
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.retail_changed",
                    order.Id.ToString(), "success", now);
            }

            if (floorChanged)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PricingChanged, updatedBy,
                    OrderChangeSummary.GlobalScaleFloorChanged(command.GlobalScaleFloor), now));
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.global_scale_changed",
                    order.Id.ToString(), "success", now);
            }

            if (shippingChanged)
            {
                // PartiesChanged: el envío es la entrega del pedido, y el pedido no edita sus partes (H5). Un
                // cambio que sólo mueve la tasa no tiene texto y no deja fila, pero sí se audita.
                if (shippingSummary is not null)
                {
                    orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                        OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PartiesChanged, updatedBy,
                        shippingSummary, now));
                }

                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.shipping_changed",
                    order.Id.ToString(), "success", now);
            }

            if (notesChanged)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.NotesChanged, updatedBy,
                    OrderChangeSummary.NotesChanged(), now));
            }

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

            if (proofsChanged)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PaymentProofsChanged, updatedBy,
                    OrderChangeSummary.PaymentProofsChanged(attachedInputs.Length, corrections.Count, removedProofIds.Length),
                    now));
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

            // Antes de mirar el total: las líneas que cambiaron mueven el tramo de las que agrupan
            // con ellas, y el detal o el piso cambian todos los descuentos.
            await OrderPricingRecalculation.ApplyAsync(
                pricingLookup, settingsStore, command.TenantId, order, now, cancellationToken);
            order.RecalculatePaymentStatus(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await copies.RollbackAsync();
            throw;
        }

        return await OrderDetails.LoadAsync(
            orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
    }

    private async Task RecordItemEditsAsync(
        Guid tenantId,
        Order order,
        IReadOnlyList<OrderItemEdit> edits,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // El nombre de un producto que se quitó se resuelve contra el catálogo: la línea ya no está
        // para decirlo.
        var removedVariantIds = edits
            .Where(edit => edit.Kind == OrderItemEditKind.Removed)
            .Select(edit => edit.VariantId)
            .ToArray();
        var removedProducts = removedVariantIds.Length == 0
            ? new Dictionary<Guid, QuotationVariantRef>()
            : await productLookup.FindManyAsync(tenantId, removedVariantIds, cancellationToken);

        foreach (var edit in edits)
        {
            string summary;
            string action;
            if (edit.Kind == OrderItemEditKind.Added)
            {
                summary = OrderChangeSummary.ItemAdded(edit.ProductName!, edit.Quantity!.Value);
                action = "quotation.order.item_added";
            }
            else if (edit.Kind == OrderItemEditKind.QuantityChanged)
            {
                summary = OrderChangeSummary.ItemQuantityChanged(
                    edit.ProductName!, edit.PreviousQuantity!.Value, edit.Quantity!.Value);
                action = "quotation.order.item_updated";
            }
            else
            {
                var productName = removedProducts.TryGetValue(edit.VariantId, out var product)
                    ? product.Name
                    : "un producto";
                summary = OrderChangeSummary.ItemRemoved(productName);
                action = "quotation.order.item_removed";
            }

            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.ItemsChanged, updatedBy, summary, now));
            auditPublisher.Publish(
                tenantId, executionContext.SubjectId, action, order.Id.ToString(), "success", now);
        }
    }
}
