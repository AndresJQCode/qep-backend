namespace Modules.Scheduling.Application;

/// <summary>
/// Sobre qué recursos puede actuar quien pide (spec 2026-11-21, D-7). Con <c>scheduling.agenda.read_all</c> son todos los del tenant;
/// sin él, sólo los vinculados a su membresía. Lo resuelve el servidor y lo aplica el repositorio en la consulta: el cliente nunca
/// filtra (regla BFF). Un recurso fuera de alcance responde 403, nunca 404.
/// </summary>
public sealed class ResourceScope
{
    private readonly HashSet<Guid>? _resourceIds;

    private ResourceScope(HashSet<Guid>? resourceIds) => _resourceIds = resourceIds;

    public static ResourceScope All { get; } = new(null);

    public bool IsAll => _resourceIds is null;

    /// <summary>Los ids del alcance. Un alcance «todos» no tiene lista: quien filtre por ids mira <see cref="IsAll"/> primero, o
    /// terminaría pidiendo la lista vacía y no viendo nada.</summary>
    public IReadOnlyCollection<Guid> ResourceIds =>
        _resourceIds ?? throw new InvalidOperationException("The all-resources scope has no list of ids; check IsAll first.");

    public static ResourceScope Only(IEnumerable<Guid> resourceIds) => new([.. resourceIds]);

    public static ResourceScope For(bool readAll, IEnumerable<Guid> linkedResourceIds) =>
        readAll ? All : Only(linkedResourceIds);

    public bool Includes(Guid resourceId) => _resourceIds is null || _resourceIds.Contains(resourceId);
}
