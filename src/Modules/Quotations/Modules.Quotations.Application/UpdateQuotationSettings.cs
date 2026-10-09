using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record UpdateQuotationSettingsCommand(
    Guid TenantId,
    int MinimumUnits,
    IReadOnlyDictionary<string, decimal>? MinimumTotals) : ICommand<QuotationSettingsDto>;

public sealed class UpdateQuotationSettingsValidator : AbstractValidator<UpdateQuotationSettingsCommand>
{
    public UpdateQuotationSettingsValidator()
    {
        RuleFor(command => command.MinimumUnits).GreaterThanOrEqualTo(1);
        RuleFor(command => command.MinimumTotals).NotNull();
        // One failure per bad row, keyed MinimumTotals.<CODE> (same rules as product prices).
        RuleFor(command => command.MinimumTotals).Custom((totals, context) =>
        {
            if (totals is null)
            {
                return;
            }

            foreach (var failure in CurrencyAmountRules.Check(totals, context.PropertyPath))
            {
                context.AddFailure(failure);
            }
        });
    }
}

/// <summary>Whole-document replace: a code absent from the body deletes its total. Last write
/// wins, serialized per tenant, no <c>If-Match</c>: the spec gives this endpoint no version, and
/// the form sends the whole document.</summary>
public sealed class UpdateQuotationSettingsHandler(
    IQuotationSettingsStore store,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateQuotationSettingsCommand> validator)
    : ICommandHandler<UpdateQuotationSettingsCommand, QuotationSettingsDto>
{
    public const string AuditAction = "quotations.settings.updated";

    public async Task<QuotationSettingsDto> HandleAsync(
        UpdateQuotationSettingsCommand command, CancellationToken cancellationToken)
    {
        // Authorise before validating: a caller from another tenant must not get the field map.
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var settings = QuotationSettings.Create(command.MinimumUnits, command.MinimumTotals!);

        // The store locks the tenant's settings row inside this transaction and holds it until the
        // commit, so two PUTs for the same tenant run one after the other: each replaces the whole
        // document it read under the lock, and the last one wins without merging the two bodies.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await store.SaveAsync(command.TenantId, settings, cancellationToken);

        var now = clock.UtcNow;
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            AuditAction,
            command.TenantId.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return QuotationSettingsMapping.ToDto(settings);
    }
}
