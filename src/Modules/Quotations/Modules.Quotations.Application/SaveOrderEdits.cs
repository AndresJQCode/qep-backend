using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

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
    ShippingRequest? Shipping = null,
    OrderBillingEditRequest? Billing = null,
    IReadOnlyList<Guid>? WithholdingTaxRateIds = null,
    QuotationBillingAccountRequest? BillingAccount = null,
    OperationCityChoice OperationCity = default,
    IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null,
    IReadOnlyList<string>? ClearedWithholdingTypes = null,
    DateOnly? OrderDate = null,
    IReadOnlyList<OrderItemAddition>? Gifts = null,
    OrderDeliveryEditRequest? Delivery = null) : ICommand<OrderDetailDto>, IOrderEdits;

public sealed class SaveOrderEditsValidator : OrderEditsValidator<SaveOrderEditsCommand>
{
    public SaveOrderEditsValidator()
        : base(requireFileIds: true, requireBillingIdentification: true)
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
    ITenantPriceRounding priceRounding,
    IQuotationProductLookup productLookup,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    ShippingResolver shippingResolver,
    IMembershipDirectory membershipDirectory,
    ITenantDirectory tenantDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SaveOrderEditsCommand> validator,
    IQuotationCompanyLookup companyLookup,
    IQuotationWithholdingLookup withholdingLookup,
    ITenantClock tenantClock,
    IOrderVariantCostLookup costLookup,
    IQuotationGeographyLookup geographyLookup,
    IWholesaleLinePricing wholesalePricing,
    IQuotationCustomerLookup customerLookup,
    IExternalOrderDeliveryCheck deliveryCheck,
    IQuotationLifecycleEventPublisher lifecycleEvents)
    : ICommandHandler<SaveOrderEditsCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        SaveOrderEditsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        // Un pedido de proyecto se edita sin líneas (spec 2026-11-26 §12-C): con la lista vacía hay que saber el tipo antes
        // de validar.
        var orderId = new OrderId(command.OrderId);
        var order = command.Items.Count == 0
            ? await orderRepository.FindByIdAsync(command.TenantId, orderId, cancellationToken)
            : null;
        await OrderEditsValidation.ValidateAsync(validator, command, order, cancellationToken);

        order ??= await orderRepository.FindByIdAsync(command.TenantId, orderId, cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);
        // Con entrega en el cuerpo, el motivo exacto (facturado o preparado) gana a la guarda general de abajo.
        if (command.Delivery is not null)
        {
            order.EnsureDeliveryEditable();
        }

        if (!order.IsEditable)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.NotEditable,
                "An order can only be edited while it is open, not prepared and not invoiced.");
        }

        if (command.Proofs.RemoveIds.Count > 0)
        {
            PaidOrderProofRemoval.EnsureAuthorized(executionContext, command.TenantId, order);
        }

        PaidOrderProofRemoval.EnsureAmountCorrectionAuthorized(
            executionContext,
            command.TenantId,
            order,
            command.Proofs.Update.Select(update => (update.ProofId, update.Amount)));

        // El If-Match se comprueba contra lo cargado, antes de tocar nada: un 412 no deja ni la
        // copia pública de un comprobante publicada.
        if (order.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The order changed after it was loaded.");
        }

        // Un pedido de proyecto no cambia de líneas ni de regalos, e ignora detal, piso de escala y envío del cuerpo (spec
        // 2026-11-26 §12-C). Antes de publicar nada.
        var keepsProjectLine = ProjectOrderLines.KeepsTheLine(order, command.Items, command.Gifts);

        // El neto de antes de tocar nada: el recálculo del pago compara contra él (spec 2026-11-10 §3.2).
        var previousNetTotal = order.NetTotal;
        // Los comprobantes nuevos en dos lecturas, no dos por comprobante. Los reemplazos siguen de a uno: cada uno
        // excluye su propio comprobante de la pregunta de uso, y el lote sólo admite una exclusión.
        await OrderPaymentProofResolver.ResolveManyAsync(
            fileLookup,
            orderRepository,
            command.TenantId,
            command.Proofs.Add.Select(addition => addition.FileId!.Value).ToArray(),
            exceptProofId: null,
            cancellationToken);

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
        var format = await HistoryFormat.ReadAsync(tenantDirectory, command.TenantId, cancellationToken);
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
            IReadOnlyList<OrderItemEdit> itemEdits = [];
            if (!keepsProjectLine)
            {
                var paidItemEdits = await OrderItemEdits.ApplyAsync(
                    order, command.Items, pricingLookup, costLookup, wholesalePricing, command.TenantId, updatedBy, now,
                    command.IsRetail, cancellationToken);
                // Spec 2026-11-23: los regalos van después de las pagadas y sólo si el cuerpo los trae (null no toca nada).
                // Sus ediciones se suman a las de las líneas: «nada cambió», el historial y las fotos de producto las ven
                // igual.
                var giftEdits = await OrderGiftEdits.ApplyAsync(
                    order, command.Gifts, pricingLookup, costLookup, command.TenantId, updatedBy, now, cancellationToken);
                itemEdits = [.. paidItemEdits, .. giftEdits];
            }

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
            // Spec 2026-11-16 §4: ausente no la toca; el tope es hoy en el calendario del tenant, que se lee sólo si llegó.
            var previousOrderDate = order.OrderDate;
            var orderDateChanged = command.OrderDate is { } orderDate
                && order.ChangeOrderDate(
                    orderDate, (await tenantClock.GetAsync(command.TenantId, cancellationToken)).Today, now);
            var retailChanged = !keepsProjectLine && order.IsRetail != command.IsRetail;
            if (retailChanged)
            {
                order.SetIsRetail(command.IsRetail, updatedBy, now);
            }

            var floorChanged = !keepsProjectLine && order.GlobalScaleFloor != command.GlobalScaleFloor;
            if (floorChanged)
            {
                order.SetGlobalScaleFloor(command.GlobalScaleFloor, updatedBy, now);
            }

            // La entrega (spec 2026-11-24 §10), antes de la facturación, las retenciones, el envío y el estado de pago.
            var deliveryBefore = OrderDeliveryState.Of(order);
            var shippingBeforeDelivery = order.ShippingDetails;
            var deliveryChanged = await OrderDeliveryEdits.ApplyAsync(
                order, command.Delivery, strict: true, customerLookup, geographyLookup, deliveryCheck, command.TenantId,
                updatedBy, now, cancellationToken);
            var deliverySummary = deliveryChanged
                ? await OrderDeliveryEdits.DescribeChangeAsync(
                    geographyLookup, deliveryBefore, OrderDeliveryState.Of(order), cancellationToken)
                : null;

            // La facturación, en la misma escritura que el envío y antes de recalcular el estado de pago: una
            // retención nueva baja el neto que los comprobantes cubren. La fila existente se modifica, no se
            // reemplaza (IX_order_parties_order_role).
            string? billingSummary = null;
            if (command.Billing is not null)
            {
                var regional = await tenantDirectory.GetRegionalSettingsAsync(
                    new TenantId(command.TenantId), cancellationToken);
                await PartyRegionRules.EnsureAsync(
                    geographyLookup, command.Billing.Party.ToDomain(), "Parties.Billing", cancellationToken);
                billingSummary = OrderBillingEdits.Apply(
                    order, command.Billing, regional?.Country, strict: true, updatedBy, now);
                await BillingPersonTypeRequirement.EnsureForOrderAsync(
                    tenantDirectory, command.TenantId, order, cancellationToken);
            }

            var billingChanged = billingSummary is not null;

            // La empresa y los selectores de retención (spec impuestos por tipo D21, D22), también antes de recalcular el
            // estado de pago: una retención baja el neto que los comprobantes cubren.
            var withholdingEdits = await OrderWithholdingEdits.ApplyAsync(
                order, command,
                new OrderWithholdingEditsContext(
                    companyLookup, withholdingLookup, geographyLookup, tenantClock, command.TenantId, shippingResolver),
                updatedBy, now, cancellationToken);
            var accountChanged = withholdingEdits.BillingAccountSummary is not null;
            var municipalTexts = await OrderMunicipalHistory.DescribeAsync(
                geographyLookup, withholdingEdits, format, cancellationToken);
            var withholdingsChanged = withholdingEdits.Changed;

            // El envío con la moneda del pedido, que no cambia después de creado (P5), y con la recogida que dejó la
            // entrega. Antes de RecalculatePaymentStatus, más abajo: un envío cobrado sube el neto que los comprobantes
            // cubren.
            // Pasar a recoger ya limpió el envío en SetDelivery: ese cambio se cuenta contra lo que había antes.
            var clearedByPickup = deliveryChanged && order.IsStorePickup && shippingBeforeDelivery != ShippingDetails.None;
            var shippingBefore = clearedByPickup ? shippingBeforeDelivery : order.ShippingDetails;
            var shipping = keepsProjectLine
                ? null
                : await shippingResolver.ResolveAsync(
                    command.TenantId, command.Shipping, order.ShippingDetails, order.IsStorePickup, order.Currency,
                    order.ChargesVat, "order.order", cancellationToken);
            var shippingResolved = shipping is not null && shipping != order.ShippingDetails;
            if (shippingResolved)
            {
                order.SetShipping(shipping!, updatedBy, now);
            }

            var shippingChanged = clearedByPickup || shippingResolved;
            // Contra lo que quedó en el agregado (ya validado), no contra lo que trajo el resolver.
            var shippingSummary = shippingChanged
                ? OrderChangeSummary.ShippingChanged(shippingBefore, order.ShippingDetails, format)
                : null;

            // Spec retenciones por UVT §5.4: la UVT vigente, después de las ediciones y antes de recalcular el estado de pago
            // (una retención que deja de aplicar sube el neto). Sin historial propio, pero se guarda aunque sea lo único que
            // cambió: si no, el pedido seguiría con la UVT vieja después de «guardar».
            var taxUnitsChanged = await TaxUnitRefresh.RefreshAsync(
                order, withholdingLookup, TaxUnitRefresh.DocumentDate(tenantClock, command.TenantId, order.CreatedAt),
                cancellationToken);

            var proofsChanged = removedProofIds.Length > 0 || corrections.Count > 0 || attachedInputs.Length > 0;
            if (itemEdits.Count == 0 && !proofsChanged && !notesChanged && !orderDateChanged && !floorChanged && !retailChanged
                && !deliveryChanged && !shippingChanged && !billingChanged && !accountChanged && !withholdingsChanged
                && !taxUnitsChanged)
            {
                // Nada cambió: ni versión nueva ni historial. El cliente recibe lo que ya tenía.
                return await OrderDetails.LoadAsync(
                    orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
            }

            await RecordItemEditsAsync(command.TenantId, order, itemEdits, format, updatedBy, now, cancellationToken);
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
                    OrderChangeSummary.GlobalScaleFloorChanged(command.GlobalScaleFloor, format), now));
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.global_scale_changed",
                    order.Id.ToString(), "success", now);
            }

            // PartiesChanged: recoger o enviar y la dirección (spec 2026-11-24 §10.4). El envío, abajo, deja su propia
            // fila: si en el mismo guardado cambian los dos, salen las dos.
            if (deliverySummary is not null)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PartiesChanged, updatedBy,
                    deliverySummary, now));
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.delivery_changed",
                    order.Id.ToString(), "success", now);
            }

            // Spec 2026-11-24 §11.1.1: la dirección nueva de un pedido de Shopify sale al outbox en la misma transacción que
            // el pedido, y Shopify encola su envío. Recoger no publica: no hay dirección que mandar.
            if (order.Source == OrderSource.Shopify
                && OrderDeliveryEdits.OwnShippingPartyChanged(order, deliveryBefore.Shipping))
            {
                lifecycleEvents.PublishShippingPartyChanged(order, now);
            }

            if (shippingChanged)
            {
                // PartiesChanged: el envío es parte de la entrega del pedido. Un cambio que sólo mueve la tasa no
                // tiene texto y no deja fila, pero sí se audita.
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

            if (billingChanged)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PartiesChanged, updatedBy,
                    billingSummary!, now));
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.billing_changed",
                    order.Id.ToString(), "success", now);
            }

            if (accountChanged)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PartiesChanged, updatedBy,
                    withholdingEdits.BillingAccountSummary!, now));
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.billing_account_changed",
                    order.Id.ToString(), "success", now);
            }

            // Spec ReteICA por empresa §7.8: el cambio de municipio, como el de la cuenta, es un PartiesChanged.
            if (municipalTexts.OperationCitySummary is { } citySummary)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PartiesChanged, updatedBy,
                    citySummary, now));
            }

            if (municipalTexts.WithholdingsSummary is { } withholdingsSummary)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PartiesChanged, updatedBy,
                    withholdingsSummary, now));
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.withholdings_changed",
                    order.Id.ToString(), "success", now);
            }

            // Una entrada por tarifa descartada, quitada o editada, cada una un microsegundo después de la anterior: el
            // historial ordena por fecha y el orden es parte de lo que cuenta (spec §7.7).
            var position = 0;
            foreach (var text in municipalTexts.RateEdited)
            {
                var at = now.AddTicks(TimeSpan.TicksPerMicrosecond * ++position);
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.WithholdingRateEdited, updatedBy, text, at));
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, OrderMunicipalHistory.RateEditedAuditAction,
                    order.Id.ToString(), "success", at);
            }

            if (notesChanged)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.NotesChanged, updatedBy,
                    OrderChangeSummary.NotesChanged(), now));
            }

            if (orderDateChanged)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.OrderDateChanged, updatedBy,
                    OrderChangeSummary.OrderDateChanged(previousOrderDate, order.OrderDate), now));
                // Owner, 2026-11-16: la fecha mueve el pedido en filtros, exportación y reportes, así que el cambio
                // también queda en la auditoría, no sólo en el historial del pedido.
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.order_date_changed",
                    order.Id.ToString(), "success", now);
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
                pricingLookup, settingsStore, priceRounding, command.TenantId, order, now, cancellationToken);
            if (order.RecalculatePaymentStatus(previousNetTotal, now) is { } paymentChange)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PaymentStatusRecalculated, updatedBy,
                    OrderChangeSummary.PaymentStatusRecalculated(paymentChange, format), now));
            }

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
        HistoryFormat format,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // El nombre de un producto que se quitó se resuelve contra el catálogo: la línea ya no está
        // para decirlo.
        // Los regalos quitados no se buscan: su edición ya trae el nombre de la foto de la línea.
        var removedVariantIds = edits
            .Where(edit => edit.Kind == OrderItemEditKind.Removed && !edit.IsGift)
            .Select(edit => edit.VariantId)
            .ToArray();
        var removedProducts = removedVariantIds.Length == 0
            ? new Dictionary<Guid, QuotationVariantRef>()
            : await productLookup.FindManyAsync(tenantId, removedVariantIds, cancellationToken);

        foreach (var edit in edits)
        {
            string summary;
            string action;
            if (edit.IsGift)
            {
                (summary, action) = edit.Kind switch
                {
                    OrderItemEditKind.Added => (
                        OrderChangeSummary.GiftAdded(edit.ProductName!, edit.Quantity!.Value, format),
                        "quotation.order.gift_added"),
                    OrderItemEditKind.QuantityChanged => (
                        OrderChangeSummary.GiftQuantityChanged(
                            edit.ProductName!, edit.PreviousQuantity!.Value, edit.Quantity!.Value, format),
                        "quotation.order.gift_updated"),
                    _ => (
                        OrderChangeSummary.GiftRemoved(edit.ProductName ?? "un producto"),
                        "quotation.order.gift_removed"),
                };
            }
            else if (edit.Kind == OrderItemEditKind.Added)
            {
                summary = OrderChangeSummary.ItemAdded(edit.ProductName!, edit.Quantity!.Value, format);
                action = "quotation.order.item_added";
            }
            else if (edit.Kind == OrderItemEditKind.UnitPriceChanged)
            {
                summary = OrderChangeSummary.ItemUnitPriceChanged(
                    edit.ProductName!, edit.PreviousUnitPrice!.Value, edit.UnitPrice!.Value, format);
                action = "quotation.order.item_updated";
            }
            else if (edit.Kind == OrderItemEditKind.QuantityChanged)
            {
                summary = OrderChangeSummary.ItemQuantityChanged(
                    edit.ProductName!, edit.PreviousQuantity!.Value, edit.Quantity!.Value, format);
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
