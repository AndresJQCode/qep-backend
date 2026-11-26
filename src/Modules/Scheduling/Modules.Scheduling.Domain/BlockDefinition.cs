namespace Modules.Scheduling.Domain;

/// <summary>
/// Lo que el tenant declara de una franja (spec 2026-11-21, «Franjas»). Un tipo y no doce parámetros: la creación y la edición lo reciben
/// igual. <paramref name="ResourceIds"/> va en orden: es el orden de la asignación «primero libre».
/// </summary>
public sealed record BlockDefinition(
    string Name,
    BlockKind Kind,
    IReadOnlyList<WeekdayHours> Weekly,
    DateHours? OneOff,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool ClosesOnHolidays,
    int CapacityPerResource,
    SessionMode? SessionMode,
    bool AllowClientChoice,
    AssignmentRule AssignmentRule,
    IReadOnlyList<ServiceId> ServiceIds,
    IReadOnlyList<ResourceId> ResourceIds);
