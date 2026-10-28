using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>Plan 2026-10-21: habilitar o deshabilitar un proveedor de un tenant desde la consola. Uno por
/// pedido y no un lote como los módulos: un proveedor no tiene dependencias, así que no hay cascada que
/// mandar junta. Texto a propósito: se valida antes de convertir.</summary>
public sealed record ChangeTenantProviderCommand(
    TenantId TenantId,
    TenantId TargetTenantId,
    string? Provider,
    string? Status,
    string? Reason,
    string? Note) : ICommand<OperatorTenantDetailDto>;

/// <summary>Texto libre ⇒ validador, aunque el dominio valide (CLAUDE.md): el validador da el campo y el
/// dominio el código (<c>tenancy.providers.no_changes</c>, <c>tenancy.providers.reason_not_allowed</c>).
/// <c>TenantProvider.Create</c> no valida la clave, así que la que no está en el catálogo se corta acá.</summary>
public sealed class ChangeTenantProviderValidator : AbstractValidator<ChangeTenantProviderCommand>
{
    public ChangeTenantProviderValidator()
    {
        RuleFor(command => command.Provider).Must(OperatorInput.IsProviderKey).WithMessage("Unknown provider key.");
        RuleFor(command => command.Status).Must(OperatorInput.IsDirection).WithMessage("Status must be 'active' or 'inactive'.");
        RuleFor(command => command.Reason).Must(OperatorInput.IsReason).WithMessage("Unknown reason.");
        RuleFor(command => command.Note).MaximumLength(TenantChange.NoteMaxLength);
    }
}

public sealed class ChangeTenantProviderHandler(
    ITenantRepository tenantRepository,
    ITenantProviderRepository providerRepository,
    ITenantChangeRepository changeRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IOperatorTenantReader reader,
    IClock clock,
    IValidator<ChangeTenantProviderCommand> validator)
    : ICommandHandler<ChangeTenantProviderCommand, OperatorTenantDetailDto>
{
    public async Task<OperatorTenantDetailDto> HandleAsync(ChangeTenantProviderCommand command, CancellationToken cancellationToken)
    {
        // operator.modules.manage y no un permiso propio (plan 2026-10-21): mismo rol y misma consola, y un
        // permiso nuevo obligaría a tocar roles y la máscara sin ganar nada.
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, OperatorPermissions.ModulesManage, operatorTenant);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Mismo candado que los módulos y el estado: serializa toda operación de la consola sobre el tenant y
        // se toma antes de leer nada, el tenant incluido.
        await using (var scope = await unitOfWork.BeginTenantChangeScopeAsync(command.TargetTenantId, cancellationToken))
        {
            _ = await tenantRepository.GetAsync(command.TargetTenantId, cancellationToken)
                ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");

            // El validador ya garantizó que el texto es conocido: estas conversiones no lanzan.
            var providerKey = command.Provider!;
            var to = TenantChangeVocabulary.ParseModuleStatus(command.Status!);
            var reason = TenantChangeVocabulary.ParseReason(command.Reason!);
            // El motivo antes que el "sin cambios", en el mismo orden que TenantModuleChangeBatch.Plan.
            TenantProviderChange.EnsureReasonAllowed(to, reason);

            var rows = await providerRepository.ListByTenantAsync(command.TargetTenantId, cancellationToken);
            var row = rows.SingleOrDefault(value => string.Equals(value.ProviderKey, providerKey, StringComparison.Ordinal));
            TenantModuleStatus? from = row?.Status;
            // Sin cambios vacíos, como los módulos: habilitar lo activo, deshabilitar lo inactivo o lo que no
            // tiene fila. Se corta acá para que tampoco quede historial ni auditoría.
            if ((to == TenantModuleStatus.Active && from == TenantModuleStatus.Active) ||
                (to == TenantModuleStatus.Inactive && from != TenantModuleStatus.Active))
            {
                throw new TenantDomainException(
                    TenantProviderChange.NoChangesCode,
                    $"The '{providerKey}' provider is already {TenantChangeVocabulary.ToText(to)} or has no row.");
            }

            var now = clock.UtcNow;
            if (row is null)
            {
                // Habilitar sin fila la crea; si otro camino la inserta a la vez, la PK lo traduce al mismo
                // no_changes (TenancyUnitOfWork).
                providerRepository.Add(TenantProvider.Create(command.TargetTenantId, providerKey, now));
            }
            else if (to == TenantModuleStatus.Active)
            {
                row.Activate(now);
            }
            else
            {
                row.Deactivate(now);
            }

            changeRepository.Add(TenantChange.ForProvider(
                command.TargetTenantId, Guid.CreateVersion7(), providerKey, from, to, reason, command.Note,
                executionContext.SubjectId, now));

            // Mismo formato que tenancy.tenant_modules.changed: tenantId = el destino, "clave:de->a" con "none"
            // sin fila (el mismo texto que el detalle) y el motivo.
            auditRecorder.Record(
                command.TargetTenantId.Value,
                executionContext.SubjectId,
                "tenancy.tenant_providers.changed",
                "tenant",
                command.TargetTenantId.Value.ToString(),
                "success",
                [
                    $"{providerKey}:{(from is { } previous ? TenantChangeVocabulary.ToText(previous) : OperatorTenantModuleDto.NoRowStatus)}->{TenantChangeVocabulary.ToText(to)}",
                    $"reason:{TenantChangeVocabulary.ToText(reason)}",
                ],
                now);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        // Después del commit: el detalle actualizado es la respuesta (la SPA reemplaza su caché con él).
        return await OperatorTenantDetails.LoadAsync(reader, operatorTenant, command.TargetTenantId, cancellationToken);
    }
}
