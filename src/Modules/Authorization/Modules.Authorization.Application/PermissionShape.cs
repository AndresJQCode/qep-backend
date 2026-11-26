namespace Modules.Authorization.Application;

/// <summary>
/// Dónde cae un permiso en la matriz de roles (spec 2026-11-16): la fila es la entidad y la columna,
/// la acción. Se declara junto al permiso y no se parsea de la clave, porque las claves de dos partes
/// (<c>advisorship.read</c>) no se pueden partir y una excepción escondida en un parser no se nota al
/// agregar un permiso.
/// </summary>
/// <param name="Entity">Clave estable de la fila, única en todo el catálogo: <c>módulo.entidad</c>.</param>
/// <param name="EntityLabel">Nombre de la fila en pantalla, en español neutro.</param>
/// <param name="Action">
/// Lo que hace el permiso, no el verbo de su clave: <c>tenancy.settings.update</c> es <c>manage</c>.
/// Si no está en <see cref="PermissionVocabulary.ColumnActions"/>, el permiso va a «Otros» de su fila.
/// </param>
public sealed record PermissionShape(string Entity, string EntityLabel, string Action);
