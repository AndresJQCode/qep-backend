namespace Modules.Coordinadora.Domain;

/// <summary>
/// El estado de una guía pedida a Coordinadora (spec 2026-11-21 §5.5). <see cref="Pending"/>: la fila está guardada y la petición en
/// camino. <see cref="Created"/>: Coordinadora devolvió el número de guía. <see cref="Rejected"/>: Coordinadora la rechazó (no la creó).
/// <see cref="Unknown"/>: timeout, 5xx o red; no se sabe si existe y la resuelve una persona (no hay clave de idempotencia). <see cref="Failed"/>:
/// la persona miró y no existe. <see cref="Cancelled"/>: se anuló en Coordinadora. La columna guarda el nombre.
/// </summary>
public enum CoordinadoraShipmentStatus
{
    Pending,
    Created,
    Rejected,
    Unknown,
    Failed,
    Cancelled,
}
