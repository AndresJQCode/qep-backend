namespace BuildingBlocks.Application;

/// <summary>
/// Cómo un módulo declara que todavía conserva una huella de un usuario. Identity la consulta
/// antes de borrar físicamente un usuario huérfano (el que se quedó sin membresía, ver
/// <c>OrphanUserCleanupWorker</c>) y no borra mientras alguna sonda responda <c>true</c>.
/// </summary>
/// <remarks>
/// <para>Vive en BuildingBlocks y no en Identity a propósito: Identity resuelve
/// <c>IEnumerable&lt;IUserReferenceProbe&gt;</c> y decide sin referenciar a ningún módulo de
/// negocio, y cada módulo registra la suya sin referenciar a Identity. Es la misma idea que
/// una FK con <c>RESTRICT</c>, que acá no puede existir porque ningún esquema referencia
/// tablas de otro (ArchitectureTests lo verifica).</para>
/// <para>Sólo cuenta lo que retiene: una referencia append-only con snapshot (auditoría,
/// notificaciones) no es huella, porque sobrevive sin el usuario. Un módulo que no guarda nada
/// del usuario simplemente no registra sonda.</para>
/// </remarks>
public interface IUserReferenceProbe
{
    /// <summary>Nombre del módulo que responde, para el log de por qué se retuvo al usuario.</summary>
    string Source { get; }

    Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken);
}
