using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ConvertQuotationToOrderCommand(
    Guid TenantId,
    Guid QuotationId,
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs) : ICommand<OrderDetailDto>;

public sealed class ConvertQuotationToOrderValidator : AbstractValidator<ConvertQuotationToOrderCommand>
{
    private static readonly string[] ValidPaymentStatuses =
        Enum.GetNames<OrderPaymentStatus>();

    public ConvertQuotationToOrderValidator()
    {
        RuleFor(command => command.PaymentStatus)
            .Must(status => ValidPaymentStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage(
                $"PaymentStatus must be one of: {string.Join(", ", ValidPaymentStatuses)}.");
        // Las notas del pedido no tienen tope (plan 2026-10-15, P2): heredan las de la cotización.
        RuleForEach(command => command.PaymentProofs).SetValidator(new OrderPaymentProofRequestValidator());

        // Revisión final (I2): un archivo, un adjunto (D16). Dos veces el mismo archivo tendría dos copias
        // públicas con claves distintas, y Storage sólo registra una.
        RuleFor(command => command.PaymentProofs)
            .Must(proofs => proofs.Select(proof => proof.FileId).Distinct().Count() == proofs.Count)
            .WithMessage("The same file cannot be attached more than once in a request.")
            .When(command => command.PaymentProofs is not null);
    }
}

internal sealed class OrderPaymentProofRequestValidator : AbstractValidator<OrderPaymentProofRequest>
{
    public OrderPaymentProofRequestValidator()
    {
        RuleFor(proof => proof.FileId).NotEmpty();
        RuleFor(proof => proof.Amount).GreaterThan(0m);
    }
}

public sealed class ConvertQuotationToOrderHandler(
    IQuotationRepository quotationRepository,
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationProductLookup productLookup,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IOrderNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<ConvertQuotationToOrderCommand> validator)
    : ICommandHandler<ConvertQuotationToOrderCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        ConvertQuotationToOrderCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await quotationRepository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        // US-18 rest: revalidar CUC/activo al aprobar, por si el estado del cliente cambió
        // después de enviar la cotización.
        var customer = await customerLookup.FindAsync(
            command.TenantId, quotation.ClientId, cancellationToken);
        QuotationCustomerEligibility.Ensure(customer, command.TenantId, quotation.ClientId);

        foreach (var proof in command.PaymentProofs)
        {
            await OrderPaymentProofResolver.ResolveAsync(
                fileLookup, orderRepository, command.TenantId, proof.FileId, exceptProofId: null, cancellationToken);
        }

        var convertedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // El año del pedido es el del día del tenant (spec 2026-09-17, punto 2b).
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        // El formato es un dato del tenant (spec 2026-09-17 de numeración). Un formato sin año usa
        // la fila `year = 0` del contador, que no se reinicia; con año, la fila del año, como
        // siempre. El contador no depende del prefijo: cambiar `PED-` por `PW` no reinicia la serie.
        var format = await numberingFormats.GetAsync(
            command.TenantId, DocumentNumberType.Order, cancellationToken);
        var counterYear = format.IncludeYear ? year : 0;
        var paymentStatus = Enum.Parse<OrderPaymentStatus>(command.PaymentStatus, ignoreCase: true);

        // Los números de pedido no tienen huecos. Las precondiciones del dominio se revisan antes
        // de pedir el número: una cotización ya convertida, anulada o incompleta sale 422 sin tocar
        // el contador. ConvertToOrder las vuelve a revisar más abajo, pero para entonces ya pasaron.
        quotation.EnsureConvertibleToOrder();

        // Convertir también saca del borrador: las líneas que todavía no congelaron su producto
        // (un borrador convertido sin enviarse, o una enviada antes del snapshot) lo congelan acá.
        // Antes de la transacción, igual que las copias: es una lectura a otro módulo y no tiene
        // por qué hacerse con el lock del contador tomado.
        var products = await QuotationItemProductLabel.ResolveMissingAsync(
            productLookup, command.TenantId, quotation, cancellationToken);

        // Las copias públicas de los comprobantes (spec 2026-09-15, P4 y P7) van antes del dominio,
        // porque OrderPaymentProof recibe la clave al crearse. Desde la primera copia, cualquier
        // falla —un rechazo de ConvertToOrder incluido— borra las copias y relanza: un pedido que no
        // se guardó no puede dejar comprobantes publicados.
        var copies = new PaymentProofCopies(paymentProofPublisher);
        Order order;
        try
        {
            // Antes de abrir la transacción, no adentro: es I/O contra R2, y desde que se toma el
            // número el lock de la fila del contador frena todas las conversiones del tenant hasta
            // el commit. Mientras menos se haga con el lock tomado, menos esperan los demás.
            var proofs = await copies.PublishAsync(
                command.TenantId, command.PaymentProofs, cancellationToken);

            // El contador se incrementa con SQL crudo, que sin transacción explícita se confirma
            // solo y en el acto: un 409 por el token de concurrencia, el 422 de IX_orders_quotation
            // o cualquier otra falla al guardar dejaba el número gastado y un hueco en la serie.
            // Con la transacción, el incremento y el pedido se confirman juntos; si algo falla,
            // disponerla sin commit deshace los dos y el número vuelve a estar disponible.
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            var sequence = await numberGenerator.NextAsync(command.TenantId, counterYear, cancellationToken);
            var orderNumber = DocumentNumberFormatter.Format(format, year, sequence);

            // Pasar la cotización a Converted y crear el pedido en la misma unidad de trabajo
            // (modelo-datos-cotizaciones.md §3): si guardar falla, no queda ninguna de las dos cosas.
            // ConvertToOrder valida las precondiciones antes de mutar. El historial (Approved) y la
            // auditoría (quotation.quotation.approved) no cambian de nombre: son contrato, no el
            // nombre del estado.
            quotation.ConvertToOrder(convertedBy, now, products);
            // Spec 2026-10-15 §4.4: el pedido nace como copia independiente de la cotización.
            order = Order.FromQuotation(
                OrderId.New(),
                command.TenantId,
                orderNumber,
                quotation,
                paymentStatus,
                command.Notes,
                convertedBy,
                proofs,
                now);

            orderRepository.Add(order);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(),
                order.Id,
                OrderHistoryEventType.Created,
                convertedBy,
                OrderChangeSummary.CreatedFromQuotation(quotation.QuotationNumber),
                now));
            quotationRepository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                QuotationHistoryEventType.Approved,
                convertedBy,
                QuotationChangeSummary.ConvertedToOrder(order.OrderNumber),
                now));
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "quotation.quotation.approved",
                quotation.Id.ToString(),
                "success",
                now);
            // D9 (spec 2026-09-16): en la misma unidad de trabajo que el pedido, así el evento sólo
            // existe si el pedido se guardó, y Storage nunca borra un temporal que nadie adjuntó.
            var attached = PaymentProofCopies.AttachedFrom(proofs);
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            // Spec 2026-10-13 §4.5 y D25: dentro de la transacción del contador, con la cotización ya Converted y el pedido creado.
            lifecycleEvents.PublishConverted(quotation, order, now);
            lifecycleEvents.PublishOrderCreated(order, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // Cuando se llega aquí la transacción ya se dispuso (el await using cierra dentro del
            // try), así que el rollback soltó el lock del contador antes de ir a borrar las copias
            // a R2.
            await copies.RollbackAsync();
            throw;
        }

        // Spec 2026-10-15 §8: la pantalla de conversión dibuja el resumen con esta respuesta.
        return await OrderDetails.LoadAsync(orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
    }
}
