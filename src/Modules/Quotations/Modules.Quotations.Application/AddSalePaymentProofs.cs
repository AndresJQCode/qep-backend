using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Sumar comprobantes a una venta que ya existe, y corregir el monto de los que ya tenía
/// cargados (a pedido, 2026-09), con el estado de pago que corresponda a lo cargado. Ruta
/// hermana de <see cref="ApproveSaleCommand"/> y no un campo del PATCH que no existe: la venta
/// no se edita en general, esto es un único gesto puntual —"cargué o corregí lo que hacía
/// falta"— para destrabar "Aprobar venta" cuando el pago no había quedado completo o correcto.
/// </summary>
public sealed record AddSalePaymentProofsCommand(
    Guid TenantId,
    Guid QuotationId,
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<SalePaymentProofRequest> PaymentProofs,
    IReadOnlyCollection<SalePaymentProofUpdateRequest> UpdatedProofs) : ICommand<SaleDto>;

public sealed class AddSalePaymentProofsValidator
    : AbstractValidator<AddSalePaymentProofsCommand>
{
    private static readonly string[] ValidPaymentStatuses =
        Enum.GetNames<SalePaymentStatus>();

    public AddSalePaymentProofsValidator()
    {
        RuleFor(command => command.PaymentStatus)
            .Must(status => ValidPaymentStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage(
                $"PaymentStatus must be one of: {string.Join(", ", ValidPaymentStatuses)}.");
        RuleFor(command => command.Notes)
            .MaximumLength(Sale.NotesMaxLength)
            .When(command => command.Notes is not null);
        RuleForEach(command => command.PaymentProofs).SetValidator(new SalePaymentProofRequestValidator());
        RuleForEach(command => command.UpdatedProofs).SetValidator(new SalePaymentProofUpdateRequestValidator());
    }
}

internal sealed class SalePaymentProofUpdateRequestValidator
    : AbstractValidator<SalePaymentProofUpdateRequest>
{
    public SalePaymentProofUpdateRequestValidator()
    {
        RuleFor(request => request.ProofId).NotEmpty();
        RuleFor(request => request.Amount).GreaterThan(0);
    }
}

public sealed class AddSalePaymentProofsHandler(
    ISaleRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationFileLookup fileLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddSalePaymentProofsCommand> validator)
    : ICommandHandler<AddSalePaymentProofsCommand, SaleDto>
{
    public async Task<SaleDto> HandleAsync(
        AddSalePaymentProofsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SalesPermissions.SaleManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var sale = await repository.FindByQuotationIdAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw SaleNotFound.For(command.QuotationId);

        foreach (var proof in command.PaymentProofs)
        {
            await SalePaymentProofResolver.ResolveAsync(
                fileLookup, command.TenantId, proof.FileId, cancellationToken);
        }

        var uploadedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        var paymentStatus = Enum.Parse<SalePaymentStatus>(command.PaymentStatus, ignoreCase: true);

        sale.AddPaymentProofs(
            command.PaymentProofs
                .Select(proof => new SalePaymentProofInput(proof.FileId, proof.Amount))
                .ToArray(),
            paymentStatus,
            command.Notes,
            uploadedBy,
            now,
            command.UpdatedProofs
                .Select(update => new SalePaymentProofAmountUpdate(
                    new SalePaymentProofId(update.ProofId), update.Amount))
                .ToArray());

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.sale.payment_proofs_added",
            sale.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return sale.ToDto();
    }
}
