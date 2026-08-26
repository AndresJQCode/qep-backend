using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record UpdateQuotationCommand(
    Guid TenantId,
    Guid QuotationId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationOverridesRequest? Overrides) : ICommand<QuotationDto>;

public sealed class UpdateQuotationValidator : AbstractValidator<UpdateQuotationCommand>
{
    public UpdateQuotationValidator()
    {
        RuleFor(command => command.PaymentMethod)
            .MaximumLength(Quotation.PaymentMethodMaxLength)
            .When(command => command.PaymentMethod is not null);
    }
}

public sealed class UpdateQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateQuotationCommand> validator)
    : ICommandHandler<UpdateQuotationCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        UpdateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        quotation.UpdateDetails(
            command.ValidUntil,
            command.PaymentMethod,
            command.Notes,
            command.Overrides.ToDomain(),
            updatedBy,
            now);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Edited,
            updatedBy,
            details: null,
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.updated",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
