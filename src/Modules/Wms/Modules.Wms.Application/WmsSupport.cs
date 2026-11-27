using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wms.Domain;

namespace Modules.Wms.Application;

/// <summary>
/// Tenant de la ruta distinto del activo, o permiso faltante → 403 <c>authorization.denied</c>, nunca 404. Después, la capacidad
/// <c>wms</c> → 403 <c>tenancy.module_not_enabled</c>. A diferencia de Loans es inyectable y no estática (S18): <c>ownerId</c> y
/// <c>warehouseId</c> son la firma definitiva para cuando la fase 3 restrinja por alcance, y hasta entonces se ignoran —
/// <c>member_scopes</c> nace vacía y nadie la lee—, así que los handlers de WMS-D no tendrán que cambiar la llamada.
/// </summary>
public class WmsAuthorization(ITenantModules tenantModules)
{
    public virtual async Task EnsureAsync(
        IExecutionContext ctx,
        Guid tenantId,
        string permission,
        Guid? ownerId = null,
        Guid? warehouseId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.TenantId.Value != tenantId || !ctx.HasPermission(permission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Wms, ct);
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this wms operation for this tenant.");
}

/// <summary>La membresía activa de quien llama: a quién se le atribuye un cambio. Copia de <c>PipelineCaller</c>.</summary>
public sealed class WmsCaller(IMembershipDirectory membershipDirectory, IExecutionContext executionContext)
{
    private Guid? _tenantId;
    private Guid? _memberId;

    public async Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_tenantId != tenantId)
        {
            _memberId = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken);
            _tenantId = tenantId;
        }

        return _memberId;
    }

    /// <summary>Sin membresía activa no hay a quién atribuir el cambio: 403.</summary>
    public async Task<Guid> RequireAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await FindAsync(tenantId, cancellationToken) ?? throw WmsAuthorization.Denied();
}

// Las búsquedas siempre están acotadas al tenant del llamador: «no encontrado» significa «no está entre tus recursos». Uno de otro
// tenant no llega hasta acá, la autorización lo frena antes con 403.
internal static class WmsNotFound
{
    public static ResourceNotFoundException Warehouse(Guid id) =>
        new(WmsErrorCodes.WarehouseNotFound, $"Warehouse '{id}' was not found.");

    public static ResourceNotFoundException Location(Guid id) =>
        new(WmsErrorCodes.LocationNotFound, $"Location '{id}' was not found.");

    public static ResourceNotFoundException Owner(Guid id) =>
        new(WmsErrorCodes.OwnerNotFound, $"Owner '{id}' was not found.");

    public static ResourceNotFoundException Incident(Guid id) =>
        new(WmsErrorCodes.IncidentNotFound, $"Incident '{id}' was not found.");

    public static ResourceNotFoundException Adjustment(Guid id) =>
        new(WmsErrorCodes.AdjustmentNotFound, $"Adjustment '{id}' was not found.");

    public static ResourceNotFoundException Movement(Guid id) =>
        new(WmsErrorCodes.MovementNotFound, $"Movement '{id}' was not found.");

    public static ResourceNotFoundException ImportPreview(Guid id) =>
        new(WmsErrorCodes.ImportPreviewNotFound, $"Import preview '{id}' was not found.");

    public static ResourceNotFoundException Task(Guid id) =>
        new(WmsErrorCodes.TaskNotFound, $"Task '{id}' was not found.");

    public static ResourceNotFoundException TaskLine(Guid id) =>
        new(WmsErrorCodes.TaskNotFound, $"Task line '{id}' was not found.");
}

internal static class WmsVersions
{
    /// <summary>La versión que el cliente leyó tiene que ser la actual: si no, 412 sin tocar nada.</summary>
    public static void Ensure(long current, long? received)
    {
        if (received != current)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The resource changed after it was loaded.");
        }
    }
}
