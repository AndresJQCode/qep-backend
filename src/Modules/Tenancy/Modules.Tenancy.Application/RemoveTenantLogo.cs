using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record RemoveTenantLogoCommand(
    TenantId TenantId,
    long ExpectedVersion,
    string CorrelationId) : ICommand<TenantSettingsDto>;

public sealed class RemoveTenantLogoValidator : AbstractValidator<RemoveTenantLogoCommand>
{
    public RemoveTenantLogoValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class RemoveTenantLogoHandler(
    ITenantRepository tenantRepository,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    ITenantLogoStorage logoStorage,
    IValidator<RemoveTenantLogoCommand> validator)
    : ICommandHandler<RemoveTenantLogoCommand, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(
        RemoveTenantLogoCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var tenant = await tenantRepository.GetAsync(command.TenantId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                "tenancy.tenant.not_found", "Tenant settings were not found.");

        if (tenant.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "Tenant settings changed after they were loaded.");
        }

        if (tenant.LogoFileId is not { } fileId)
        {
            return tenant.ToSettingsDto(logoStorage);
        }

        // Decisión 8 del spec: retirar la copia pública antes de commitear Tenancy. Si el commit
        // falla, la persona vuelve a quitarlo — UnpublishAsync es idempotente.
        await logoStorage.UnpublishAsync(command.TenantId.Value, fileId, cancellationToken);

        tenant.RemoveLogo(clock.UtcNow);
        var events = tenant.PullDomainEvents();
        auditRecorder.Record(
            tenant.Id.Value,
            executionContext.SubjectId,
            "tenancy.logo.removed",
            "tenant",
            tenant.Id.ToString(),
            "success",
            ["logoFileId"],
            clock.UtcNow);

        foreach (var domainEvent in events)
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return tenant.ToSettingsDto(logoStorage);
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.SettingsUpdate))
        {
            throw new RequestForbiddenException(
                "authorization.denied", "The subject cannot update tenant settings.");
        }
    }
}
