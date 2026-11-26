namespace Modules.Wms.Application;

/// <summary>Dos peticiones con la misma clave de idempotencia llegaron a la vez y la clave primaria del recibo frenó a la segunda.
/// El filtro la atrapa, relee el recibo que dejó la ganadora y repite su respuesta.</summary>
public sealed class OperationReplayedException(Guid tenantId, string key)
    : Exception($"The operation '{key}' was already recorded for tenant '{tenantId}'.")
{
    public Guid TenantId { get; } = tenantId;

    public string Key { get; } = key;
}
