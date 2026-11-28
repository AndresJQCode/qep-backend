using BuildingBlocks.Application;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>
/// Da la bodega principal del tenant en el primer evento que la necesita (spec §5.3). Corre en la transacción del consumidor y toma el
/// mismo lock por tenant que <see cref="SetPrincipalWarehouseHandler.LockKey"/>: dos eventos del primer uso a la vez, o un cambio de
/// principal, no provisionan dos. Si el tenant tiene bodegas activas, la más antigua pasa a ser principal; si no tiene ninguna activa,
/// crea una con código libre. Nunca elige una bodega inactiva. Encender el módulo no provisiona nada.
/// </summary>
public sealed class PrincipalWarehouseProvisioner(
    IWarehouseRepository warehouses,
    IWmsProvisioner provisioner,
    IWmsAuditRecorder auditRecorder,
    IWmsUnitOfWork unitOfWork,
    IClock clock)
{
    private const string BaseCode = "PRINCIPAL";
    private const string DefaultName = "Bodega principal";
    private static readonly Guid SystemActorId = Guid.Empty;

    public async Task<(Guid WarehouseId, Guid LocationId)> EnsureAsync(Guid tenantId, CancellationToken ct)
    {
        // El dueño Self también hace falta para asentar: el aprovisionador es idempotente y, con la transacción del consumidor abierta,
        // escribe dentro de ella.
        await provisioner.EnsureAsync(tenantId, ct);

        var principal = await warehouses.FindPrincipalAsync(tenantId, ct);
        if (principal is null)
        {
            await unitOfWork.LockAsync(SetPrincipalWarehouseHandler.LockKey(tenantId), ct);
            // Después del lock: quien lo tuvo antes ya commiteó su principal y esta lectura la ve.
            principal = await warehouses.FindPrincipalAsync(tenantId, ct) ?? await ProvisionAsync(tenantId, ct);
        }

        var general = principal.GeneralLocationId;
        if (general is null)
        {
            general = principal.EnsureGeneralLocation(clock.UtcNow).Id;
            await unitOfWork.FlushAsync(ct);
        }

        return (principal.Id.Value, general.Value.Value);
    }

    private async Task<Warehouse> ProvisionAsync(Guid tenantId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var all = await warehouses.ListAsync(tenantId, includeInactive: true, ct);
        var oldest = all
            .Where(warehouse => warehouse.IsActive)
            .OrderBy(warehouse => warehouse.CreatedAt)
            .ThenBy(warehouse => warehouse.Code, StringComparer.Ordinal)
            .FirstOrDefault();

        Warehouse principal;
        string action;
        if (oldest is not null)
        {
            principal = await warehouses.GetAsync(tenantId, oldest.Id, ct)
                ?? throw new InvalidOperationException("The warehouse listed a moment ago no longer exists.");
            action = WmsAuditActions.WarehouseUpdated;
        }
        else
        {
            var taken = all.Select(warehouse => warehouse.Code).ToHashSet(StringComparer.Ordinal);
            var code = BaseCode;
            for (var suffix = 1; taken.Contains(code); suffix++)
            {
                code = $"{BaseCode}-{suffix}";
            }

            principal = Warehouse.Create(WarehouseId.New(), tenantId, code, DefaultName, null, now);
            warehouses.Add(principal);
            action = WmsAuditActions.WarehouseCreated;
        }

        principal.MarkPrincipal(now);
        principal.EnsureGeneralLocation(now);
        auditRecorder.Record(
            tenantId,
            SystemActorId,
            WmsActorType.System,
            action,
            WmsAuditActions.Resources.Warehouse,
            principal.Id.Value.ToString(),
            new Dictionary<string, string?>
            {
                ["isPrincipal"] = "true",
                ["generalLocationId"] = principal.GeneralLocationId?.Value.ToString(),
            },
            now);

        // El ledger busca las ubicaciones en la base, no en el rastreador.
        await unitOfWork.FlushAsync(ct);
        return principal;
    }
}
