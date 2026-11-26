using BuildingBlocks.Application;
using FluentValidation;
using Modules.Store.Domain;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

/// <summary>Publica o despublica la tienda; el mismo manejador sirve a las dos rutas, que sólo difieren en <paramref name="Publish"/>.</summary>
public sealed record PublishStoreCommand(Guid TenantId, long ExpectedVersion, bool Publish) : ICommand<StoreSettingsDto>;

public sealed class PublishStoreValidator : AbstractValidator<PublishStoreCommand>
{
    public PublishStoreValidator() =>
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0).OverridePropertyName("expectedVersion");
}

public sealed class PublishStoreHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IOnlineStoreRepository stores,
    IStoreUnitOfWork unitOfWork,
    IStoreAuditPublisher auditPublisher,
    IClock clock,
    StoreSettingsComposer composer,
    IValidator<PublishStoreCommand> validator)
    : ICommandHandler<PublishStoreCommand, StoreSettingsDto>
{
    public async Task<StoreSettingsDto> HandleAsync(PublishStoreCommand command, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, StorePermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Una tienda sin guardar tiene versión 0 y todos los bloqueos: publicarla falla con el primero y no persiste nada.
        var now = clock.UtcNow;
        var store = await stores.FindAsync(command.TenantId, cancellationToken)
            ?? OnlineStore.CreateEmpty(command.TenantId, now);
        if (store.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The store settings changed after they were loaded.");
        }

        var wasPublished = store.IsPublished;
        if (command.Publish)
        {
            store.Publish(executionContext.SubjectId, now);
        }
        else
        {
            store.Unpublish(executionContext.SubjectId, now);
        }

        // Repetir el estado actual es idempotente: no sube la versión y no deja una entrada de auditoría que no cambió nada.
        if (store.IsPublished != wasPublished)
        {
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                command.Publish ? StoreAuditActions.Published : StoreAuditActions.Unpublished,
                StoreAuditActions.Resources.Store,
                command.TenantId.ToString(),
                "success",
                ["isPublished"],
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await composer.ComposeAsync(store, cancellationToken);
    }
}
