using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record VoidQuotationCommand(Guid TenantId, Guid QuotationId) : ICommand<QuotationDto>;

public sealed class VoidQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<VoidQuotationCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        VoidQuotationCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        var voidedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        // El propio agregado exige Draft o Sent y traduce cualquier otro estado a
        // quotation.quotation.not_editable.
        quotation.Void(voidedBy, now);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Voided,
            voidedBy,
            details: null,
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.voided",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
