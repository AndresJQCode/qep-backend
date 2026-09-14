using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Sumar comprobantes a un pedido que ya existe, y corregir el monto de los que ya tenía
/// cargados (a pedido, 2026-09), con el estado de pago que corresponda a lo cargado. Ruta
/// hermana de <see cref="ApproveOrderCommand"/> y no un campo del PATCH que no existe: el pedido
/// no se edita en general, esto es un único gesto puntual —"cargué o corregí lo que hacía
/// falta"— para destrabar "Aprobar pedido" cuando el pago no había quedado completo o correcto.
/// </summary>
public sealed record AddOrderPaymentProofsCommand(
    Guid TenantId,
    Guid QuotationId,
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    IReadOnlyCollection<OrderPaymentProofUpdateRequest> UpdatedProofs) : ICommand<OrderDto>;

public sealed class AddOrderPaymentProofsValidator
    : AbstractValidator<AddOrderPaymentProofsCommand>
{
    private static readonly string[] ValidPaymentStatuses =
        Enum.GetNames<OrderPaymentStatus>();

    public AddOrderPaymentProofsValidator()
    {
        RuleFor(command => command.PaymentStatus)
            .Must(status => ValidPaymentStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage(
                $"PaymentStatus must be one of: {string.Join(", ", ValidPaymentStatuses)}.");
        RuleFor(command => command.Notes)
            .MaximumLength(Order.NotesMaxLength)
            .When(command => command.Notes is not null);
        RuleForEach(command => command.PaymentProofs).SetValidator(new OrderPaymentProofRequestValidator());
        RuleForEach(command => command.UpdatedProofs).SetValidator(new OrderPaymentProofUpdateRequestValidator());
    }
}

internal sealed class OrderPaymentProofUpdateRequestValidator
    : AbstractValidator<OrderPaymentProofUpdateRequest>
{
    public OrderPaymentProofUpdateRequestValidator()
    {
        RuleFor(request => request.ProofId).NotEmpty();
        RuleFor(request => request.Amount).GreaterThan(0);
    }
}

public sealed class AddOrderPaymentProofsHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationFileLookup fileLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddOrderPaymentProofsCommand> validator)
    : ICommandHandler<AddOrderPaymentProofsCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        AddOrderPaymentProofsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var order = await repository.FindByQuotationIdAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw OrderNotFound.For(command.QuotationId);

        foreach (var proof in command.PaymentProofs)
        {
            await OrderPaymentProofResolver.ResolveAsync(
                fileLookup, command.TenantId, proof.FileId, cancellationToken);
        }

        var uploadedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        var paymentStatus = Enum.Parse<OrderPaymentStatus>(command.PaymentStatus, ignoreCase: true);

        order.AddPaymentProofs(
            command.PaymentProofs
                .Select(proof => new OrderPaymentProofInput(proof.FileId, proof.Amount))
                .ToArray(),
            paymentStatus,
            command.Notes,
            uploadedBy,
            now,
            command.UpdatedProofs
                .Select(update => new OrderPaymentProofAmountUpdate(
                    new OrderPaymentProofId(update.ProofId), update.Amount))
                .ToArray());

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.sale.payment_proofs_added",
            order.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
