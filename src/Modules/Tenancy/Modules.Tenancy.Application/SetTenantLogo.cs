using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record SetTenantLogoCommand(
    TenantId TenantId,
    Guid FileId,
    long ExpectedVersion,
    string CorrelationId) : ICommand<TenantSettingsDto>;

public sealed class SetTenantLogoValidator : AbstractValidator<SetTenantLogoCommand>
{
    public SetTenantLogoValidator()
    {
        RuleFor(command => command.FileId).NotEmpty();
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class SetTenantLogoHandler(
    ITenantRepository tenantRepository,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    ITenantLogoStorage logoStorage,
    IValidator<SetTenantLogoCommand> validator)
    : ICommandHandler<SetTenantLogoCommand, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(
        SetTenantLogoCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var tenant = await tenantRepository.GetAsync(command.TenantId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                "tenancy.tenant.not_found", "Tenant settings were not found.");

        // Estas tres fallas ocurren antes de tocar Storage: un 412 no deja copias públicas huérfanas.
        if (tenant.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "Tenant settings changed after they were loaded.");
        }

        if (command.FileId == tenant.LogoFileId)
        {
            return tenant.ToSettingsDto(logoStorage);
        }

        var previousFileId = tenant.LogoFileId;
        var publication = await logoStorage.PublishAsync(
            command.TenantId.Value, command.FileId, cancellationToken);

        try
        {
            tenant.SetLogo(command.FileId, publication.PublicKey, clock.UtcNow);
        }
        catch
        {
            // El agregado rechazó la asignación (p. ej. tenancy.tenant.not_active) y Tenancy nunca
            // llegó a commitear: el retiro va con la variante que sí lanza, para que una falla de
            // Storage acá no quede en silencio y el archivo nuevo no se quede huérfano sin que nadie
            // se entere.
            await logoStorage.UnpublishAsync(command.TenantId.Value, command.FileId, cancellationToken);
            throw;
        }

        var events = tenant.PullDomainEvents();
        auditRecorder.Record(
            tenant.Id.Value,
            executionContext.SubjectId,
            "tenancy.logo.updated",
            "tenant",
            tenant.Id.ToString(),
            "success",
            ["logoFileId"],
            clock.UtcNow);

        foreach (var domainEvent in events)
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // El commit de Tenancy falló (p. ej. choque de concurrencia de EF entre dos PUT
            // simultáneos que pasaron el paso de la versión esperada): acá el retiro es mejor
            // esfuerzo, para no tapar la excepción original de SaveChangesAsync con una de Storage.
            await logoStorage.TryUnpublishAsync(command.TenantId.Value, command.FileId, cancellationToken);
            throw;
        }

        if (previousFileId is { } oldFileId && oldFileId != command.FileId)
        {
            // El logo nuevo ya está commiteado: una falla acá no falla el request (decisión 7 del
            // spec). TryUnpublishAsync la registra en log y no la relanza.
            await logoStorage.TryUnpublishAsync(command.TenantId.Value, oldFileId, cancellationToken);
        }

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
