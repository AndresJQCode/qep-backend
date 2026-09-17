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
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs) : ICommand<OrderDto>;

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
        RuleFor(command => command.Notes)
            .MaximumLength(Order.NotesMaxLength)
            .When(command => command.Notes is not null);
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
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IOrderNumberGenerator numberGenerator,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ConvertQuotationToOrderCommand> validator)
    : ICommandHandler<ConvertQuotationToOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
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

        var now = clock.UtcNow;
        var sequence = await numberGenerator.NextAsync(command.TenantId, now.Year, cancellationToken);
        var orderNumber = OrderNumberFormatter.Format(now.Year, sequence);
        var paymentStatus = Enum.Parse<OrderPaymentStatus>(command.PaymentStatus, ignoreCase: true);

        // Las copias públicas de los comprobantes (spec 2026-09-15, P4 y P7) van antes del dominio,
        // porque OrderPaymentProof recibe la clave al crearse. Desde la primera copia, cualquier
        // falla —un rechazo de ConvertToOrder incluido— borra las copias y relanza: un pedido que no
        // se guardó no puede dejar comprobantes publicados.
        var copies = new PaymentProofCopies(paymentProofPublisher);
        Order order;
        try
        {
            var proofs = await copies.PublishAsync(
                command.TenantId, command.PaymentProofs, cancellationToken);

            // Pasar la cotización a Converted y crear el pedido en la misma unidad de trabajo
            // (modelo-datos-cotizaciones.md §3): si guardar falla, no queda ninguna de las dos cosas.
            // ConvertToOrder valida las precondiciones antes de mutar. El historial (Approved) y la
            // auditoría (quotation.quotation.approved) no cambian de nombre: son contrato, no el
            // nombre del estado.
            quotation.ConvertToOrder(convertedBy, now);
            order = Order.Create(
                OrderId.New(),
                command.TenantId,
                orderNumber,
                quotation.Id,
                paymentStatus,
                command.Notes,
                convertedBy,
                proofs,
                now);

            orderRepository.Add(order);
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

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await copies.RollbackAsync();
            throw;
        }

        return order.ToDto();
    }
}
