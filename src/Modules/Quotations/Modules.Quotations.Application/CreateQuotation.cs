using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record CreateQuotationCommand(
    Guid TenantId,
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationOverridesRequest? Overrides) : ICommand<QuotationDto>;

public sealed class CreateQuotationValidator : AbstractValidator<CreateQuotationCommand>
{
    public CreateQuotationValidator()
    {
        RuleFor(command => command.ClientId).NotEmpty();
        RuleFor(command => command.PaymentMethod)
            .MaximumLength(Quotation.PaymentMethodMaxLength)
            .When(command => command.PaymentMethod is not null);
    }
}

public sealed class CreateQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationNumberGenerator numberGenerator,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateQuotationCommand> validator)
    : ICommandHandler<CreateQuotationCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        CreateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar y no al revés, mismo criterio que CreateProductHandler: la
        // política del endpoint ya frena a quien le falta el permiso, pero no al que lo tiene
        // para otro tenant.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // US-1/US-18: cliente inexistente, sin CUC o inactivo bloquea la creación.
        var customer = await customerLookup.FindAsync(
            command.TenantId, command.ClientId, cancellationToken);
        QuotationCustomerEligibility.Ensure(customer, command.TenantId, command.ClientId);

        var advisorId = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        var sequence = await numberGenerator.NextAsync(command.TenantId, now.Year, cancellationToken);
        var quotationNumber = QuotationNumberFormatter.Format(now.Year, sequence);

        var quotation = Quotation.Create(
            QuotationId.New(),
            command.TenantId,
            quotationNumber,
            command.ClientId,
            advisorId,
            command.ValidUntil,
            command.PaymentMethod,
            command.Notes,
            command.Overrides.ToDomain(),
            customer.WithRetention,
            customer.VatSurplus,
            advisorId,
            now);

        repository.Add(quotation);
        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Created,
            advisorId,
            details: null,
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.created",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
