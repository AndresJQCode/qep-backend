using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Un pedido creado a mano desde Pedidos, sin cotización (spec 2026-10-15 §5.2).</summary>
public sealed record CreateOrderCommand(
    Guid TenantId,
    Guid ClientId,
    string? PaymentMethod,
    string? Notes,
    bool IsRetail,
    int? GlobalScaleFloor,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<OrderItemAddition> Items,
    string PaymentStatus,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    ShippingRequest? Shipping = null) : ICommand<OrderDetailDto>, IOrderDraft;

/// <summary>Lo que comparten crear y previsualizar un pedido: el mismo cuerpo, las mismas reglas.</summary>
public interface IOrderDraft
{
    Guid ClientId { get; }

    string? PaymentMethod { get; }

    int? GlobalScaleFloor { get; }

    QuotationPartiesRequest? Parties { get; }

    QuotationBillingAccountRequest? BillingAccount { get; }

    IReadOnlyList<OrderItemAddition> Items { get; }

    string PaymentStatus { get; }

    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs { get; }

    /// <summary>La entrega por envío (spec 2026-10-17 §5.4); null es "sin envío".</summary>
    ShippingRequest? Shipping { get; }
}

/// <summary>
/// Abstracta a propósito, como <see cref="OrderEditsValidator{TEdits}"/>: el escaneo de validadores del
/// composition root sólo registra las dos concretas. El guardado exige la cuenta de cobro, el documento de
/// la facturación propia y el archivo de cada comprobante; el cálculo previo, ninguno de los tres: los dos
/// últimos todavía no se subieron, y la pantalla de «Nuevo pedido» pinta el precio de la línea en cuanto
/// hay cliente y producto, antes de que se elija la empresa que factura.
/// </summary>
public abstract class OrderDraftValidator<TDraft> : AbstractValidator<TDraft>
    where TDraft : IOrderDraft
{
    private static readonly string[] ValidPaymentStatuses = Enum.GetNames<OrderPaymentStatus>();

    protected OrderDraftValidator(bool requireBillingAccount, bool requireBillingIdentification, bool requireFileIds)
    {
        RuleFor(draft => draft.ClientId).NotEmpty();
        RuleFor(draft => draft.PaymentMethod)
            .MaximumLength(Order.PaymentMethodMaxLength)
            .When(draft => draft.PaymentMethod is not null);
        RuleFor(draft => draft.GlobalScaleFloor)
            .GreaterThan(0)
            .When(draft => draft.GlobalScaleFloor is not null);
        RuleFor(draft => draft.BillingAccount).NotNull().When(_ => requireBillingAccount);
        RuleFor(draft => draft.Items)
            .NotNull()
            .Must(items => items.Count > 0)
            .WithMessage("At least one product is required.");
        RuleForEach(draft => draft.Items).ChildRules(item =>
        {
            item.RuleFor(line => line.VariantId).NotEmpty();
            item.RuleFor(line => line.Quantity).GreaterThan(0m);
        });
        RuleFor(draft => draft.Items)
            .Must(items => items.Select(line => line.VariantId).Distinct().Count() == items.Count)
            .WithMessage("La misma variante no puede aparecer más de una vez.")
            .When(draft => draft.Items is not null);
        RuleFor(draft => draft.PaymentStatus)
            .Must(status => ValidPaymentStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"PaymentStatus must be one of: {string.Join(", ", ValidPaymentStatuses)}.");
        RuleFor(draft => draft.PaymentProofs).NotNull();
        RuleForEach(draft => draft.PaymentProofs).ChildRules(proof =>
        {
            proof.RuleFor(value => value.FileId).NotEmpty().When(_ => requireFileIds);
            proof.RuleFor(value => value.Amount).GreaterThan(0m);
        });
        // Un archivo, un adjunto (spec 2026-09-15, D16): dos copias públicas del mismo archivo y Storage
        // sólo registra una.
        RuleFor(draft => draft.PaymentProofs)
            .Must(proofs => proofs.Select(proof => proof.FileId).Distinct().Count() == proofs.Count)
            .WithMessage("The same file cannot be attached more than once in a request.")
            .When(draft => requireFileIds && draft.PaymentProofs is not null);
        RuleFor(draft => draft.Parties!)
            .SetValidator(new QuotationPartiesRequestValidator(requireBillingIdentification))
            .When(draft => draft.Parties is not null);
        RuleFor(draft => draft.Shipping!)
            .SetValidator(new ShippingRequestValidator())
            .When(draft => draft.Shipping is not null);
    }
}

public sealed class CreateOrderValidator : OrderDraftValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
        : base(requireBillingAccount: true, requireBillingIdentification: true, requireFileIds: true)
    {
    }
}

public sealed class CreateOrderHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    OrderDraftBuilder drafts,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IOrderNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<CreateOrderCommand> validator)
    : ICommandHandler<CreateOrderCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        foreach (var proof in command.PaymentProofs)
        {
            await OrderPaymentProofResolver.ResolveAsync(
                fileLookup, orderRepository, command.TenantId, proof.FileId, exceptProofId: null, cancellationToken);
        }

        var advisorId = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // Mismo consecutivo y mismo formato que la conversión: el pedido manual no se distingue por su
        // número (spec 2026-10-15 §5.2).
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        var format = await numberingFormats.GetAsync(command.TenantId, DocumentNumberType.Order, cancellationToken);
        var counterYear = format.IncludeYear ? year : 0;
        var paymentStatus = Enum.Parse<OrderPaymentStatus>(command.PaymentStatus, ignoreCase: true);

        // Las copias públicas de los comprobantes van antes de la transacción (I/O contra R2) y se
        // deshacen si el pedido no se guarda, como en la conversión.
        var copies = new PaymentProofCopies(paymentProofPublisher);
        Order order;
        try
        {
            var proofs = await copies.PublishAsync(command.TenantId, command.PaymentProofs, cancellationToken);

            // El contador y el pedido se confirman juntos: si algo falla, el número vuelve a estar libre.
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            var sequence = await numberGenerator.NextAsync(command.TenantId, counterYear, cancellationToken);
            var orderNumber = DocumentNumberFormatter.Format(format, year, sequence);
            order = await drafts.BuildAsync(
                new OrderDraftInput(
                    command.TenantId, command.ClientId, advisorId, command.PaymentMethod, command.Notes,
                    command.IsRetail, command.GlobalScaleFloor, command.Parties, command.BillingAccount,
                    command.Items, paymentStatus, proofs, AllowIncompleteCustomer: false, command.Shipping),
                orderNumber,
                now,
                cancellationToken);

            orderRepository.Add(order);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Created, advisorId,
                OrderChangeSummary.CreatedManually(), now));
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "quotation.order.created",
                order.Id.ToString(), "success", now);
            var attached = PaymentProofCopies.AttachedFrom(proofs);
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            // Spec 2026-10-15 §4.6: el pedido sin cotización también es una venta para Pipeline.
            lifecycleEvents.PublishOrderCreated(order, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await copies.RollbackAsync();
            throw;
        }

        return await OrderDetails.LoadAsync(orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
    }
}
