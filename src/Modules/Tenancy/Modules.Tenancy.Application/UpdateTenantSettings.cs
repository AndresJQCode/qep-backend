using FluentValidation;
using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record UpdateTenantSettingsCommand(
    TenantId TenantId,
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    long ExpectedVersion,
    string CorrelationId) : ICommand<TenantSettingsDto>;

public sealed class UpdateTenantSettingsValidator
    : AbstractValidator<UpdateTenantSettingsCommand>
{
    public UpdateTenantSettingsValidator()
    {
        RuleFor(command => command.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(command => command.DefaultCulture).NotEmpty().MaximumLength(20);
        RuleFor(command => command.TimeZone).NotEmpty().MaximumLength(100);
        RuleFor(command => command.DateFormat).NotEmpty().MaximumLength(30);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class UpdateTenantSettingsHandler(
    ITenantRepository tenantRepository,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    IValidator<UpdateTenantSettingsCommand> validator)
    : ICommandHandler<UpdateTenantSettingsCommand, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(
        UpdateTenantSettingsCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var tenant = await tenantRepository.GetAsync(command.TenantId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                "tenancy.tenant.not_found",
                "Tenant settings were not found.");

        if (tenant.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "Tenant settings changed after they were loaded.");
        }

        var changed = tenant.UpdateSettings(
            command.DisplayName,
            command.DefaultCulture,
            command.TimeZone,
            command.DateFormat,
            clock.UtcNow);

        if (!changed)
        {
            return tenant.ToSettingsDto();
        }

        var events = tenant.PullDomainEvents();
        var changedFields = events
            .OfType<TenantSettingsUpdatedDomainEvent>()
            .SelectMany(domainEvent => domainEvent.ChangedFields)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        auditRecorder.Record(
            tenant.Id.Value,
            executionContext.SubjectId,
            "tenancy.settings.updated",
            "tenant",
            tenant.Id.ToString(),
            "success",
            changedFields,
            clock.UtcNow);

        foreach (var domainEvent in events)
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return tenant.ToSettingsDto();
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.SettingsUpdate))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot update tenant settings.");
        }
    }
}
