namespace Modules.Wms.Domain.Control;

/// <summary>Qué clase de novedad es (spec 2026-11-26 §8.1). Se persiste por nombre; no se renombra. Un tipo nuevo necesita su fila en
/// <see cref="IncidentCatalog"/>, su valor en <c>CK_incidents_type</c> (migración) y su propio corte.</summary>
public enum IncidentType
{
    ShortMove,
    LocationMismatch,
    UnreadableLabel,
    CountVariance,
    SyncConflict,
}
