using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ConvertQuotationToSaleCommand(
    Guid TenantId,
    Guid QuotationId,
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<SalePaymentProofRequest> PaymentProofs) : ICommand<SaleDto>;

public sealed class ConvertQuotationToSaleValidator : AbstractValidator<ConvertQuotationToSaleCommand>
{
    private static readonly string[] ValidPaymentStatuses =
        Enum.GetNames<SalePaymentStatus>();

    public ConvertQuotationToSaleValidator()
    {
        RuleFor(command => command.PaymentStatus)
            .Must(status => ValidPaymentStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage(
                $"PaymentStatus must be one of: {string.Join(", ", ValidPaymentStatuses)}.");
        RuleFor(command => command.Notes)
            .MaximumLength(Sale.NotesMaxLength)
            .When(command => command.Notes is not null);
        RuleForEach(command => command.PaymentProofs).SetValidator(new SalePaymentProofRequestValidator());
    }
}

internal sealed class SalePaymentProofRequestValidator : AbstractValidator<SalePaymentProofRequest>
{
    public SalePaymentProofRequestValidator()
    {
        RuleFor(proof => proof.FileId).NotEmpty();
        RuleFor(proof => proof.Amount).GreaterThan(0m);
    }
}

public sealed class ConvertQuotationToSaleHandler(
    IQuotationRepository quotationRepository,
    ISaleRepository saleRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationFileLookup fileLookup,
    ISaleNumberGenerator numberGenerator,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ConvertQuotationToSaleCommand> validator)
    : ICommandHandler<ConvertQuotationToSaleCommand, SaleDto>
{
    public async Task<SaleDto> HandleAsync(
        ConvertQuotationToSaleCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SalesPermissions.SaleManage);
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
            await SalePaymentProofResolver.ResolveAsync(
                fileLookup, command.TenantId, proof.FileId, cancellationToken);
        }

        var convertedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        var sequence = await numberGenerator.NextAsync(command.TenantId, now.Year, cancellationToken);
        var saleNumber = SaleNumberFormatter.Format(now.Year, sequence);
        var paymentStatus = Enum.Parse<SalePaymentStatus>(command.PaymentStatus, ignoreCase: true);

        // Validar que se pueda convertir y crear la venta en la misma unidad de trabajo
        // (modelo-datos-cotizaciones.md §3). La cotización se queda en Sent — no existe un
        // estado "aprobada"/"convertida" (ver QuotationStatus); la Sale que se crea, con su
        // QuotationId 1:1, es la única señal de que ya se convirtió.
        quotation.EnsureConvertibleToSale();
        var sale = Sale.Create(
            SaleId.New(),
            command.TenantId,
            saleNumber,
            quotation.Id,
            paymentStatus,
            command.Notes,
            convertedBy,
            command.PaymentProofs.Select(proof => new SalePaymentProofInput(proof.FileId, proof.Amount)).ToArray(),
            now);

        saleRepository.Add(sale);
        quotationRepository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Approved,
            convertedBy,
            details: null,
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.approved",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return sale.ToDto();
    }
}
