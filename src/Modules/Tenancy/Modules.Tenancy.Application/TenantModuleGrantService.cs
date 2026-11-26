using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// <see cref="ITenantModuleGrants"/>: el cuerpo de <see cref="ChangeTenantModulesHandler"/> sin el permiso de operador. Las
/// filas nuevas llevan origen <c>invitation</c> (el único que llama hoy es la aceptación de una invitación de mayorista).
/// </summary>
public sealed class TenantModuleGrantService(
    ITenantRepository tenantRepository,
    ITenantModuleRepository moduleRepository,
    ITenantChangeRepository changeRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IClock clock,
    ITenantAccessVersion tenantAccessVersion)
    : ITenantModuleGrants
{
    public async Task<IReadOnlyList<TenantModuleKey>> EnsureActiveAsync(
        TenantId tenantId,
        IReadOnlyList<TenantModuleKey> keys,
        Guid actorUserId,
        string note,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PlannedModuleChange> plan;

        // El candado serializa con la consola y con otra aceptación sobre el mismo tenant, y se toma antes de leer nada.
        await using (var scope = await unitOfWork.BeginTenantChangeScopeAsync(tenantId, cancellationToken))
        {
            _ = await tenantRepository.GetAsync(tenantId, cancellationToken)
                ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");

            var rows = await moduleRepository.ListByTenantAsync(tenantId, cancellationToken);
            var stored = rows.ToDictionary(row => row.ModuleKey, row => row.Status);

            // El lote es estricto (activar lo activo es no_changes): se le piden sólo las que faltan. Sin ninguna, no se
            // escribe nada, ni historial, ni auditoría, ni se toca la caché de permisos.
            var missing = keys
                .Distinct()
                .Where(key => !stored.TryGetValue(key, out var status) || status != TenantModuleStatus.Active)
                .Select(key => new RequestedModuleChange(key, TenantModuleStatus.Active))
                .ToArray();
            if (missing.Length == 0)
            {
                return [];
            }

            plan = TenantModuleChangeBatch.Plan(stored, missing, ChangeReason.Contract);

            var now = clock.UtcNow;
            var batchId = Guid.CreateVersion7();
            foreach (var change in plan)
            {
                var row = rows.SingleOrDefault(value => value.ModuleKey == change.Key);
                if (row is null)
                {
                    moduleRepository.Add(TenantModule.Create(tenantId, change.Key, TenantModuleSources.Invitation, now, note: null));
                }
                else
                {
                    row.Activate(now);
                }

                changeRepository.Add(TenantChange.ForModule(
                    tenantId, batchId, change.Key, change.From, change.To, ChangeReason.Contract, note, actorUserId, now));
            }

            // Misma forma que la consola: un string por cambio más el motivo, y "none" sin fila.
            auditRecorder.Record(
                tenantId.Value,
                actorUserId,
                "tenancy.tenant_modules.changed",
                "tenant",
                tenantId.Value.ToString(),
                "success",
                plan.Select(change =>
                        $"{change.Key.Value}:{(change.From is { } from ? TenantChangeVocabulary.ToText(from) : OperatorTenantModuleDto.NoRowStatus)}->{TenantChangeVocabulary.ToText(change.To)}")
                    .Append($"reason:{TenantChangeVocabulary.ToText(ChangeReason.Contract)}")
                    .ToArray(),
                now);

            // Spec 2026-11-13, D-5: el enmascarado por módulos cambia; la caché de permisos se invalida antes del commit y
            // otra vez después.
            await tenantAccessVersion.BumpAsync(tenantId.Value, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        await tenantAccessVersion.BumpAsync(tenantId.Value, cancellationToken);
        return plan.Select(change => change.Key).ToArray();
    }
}
