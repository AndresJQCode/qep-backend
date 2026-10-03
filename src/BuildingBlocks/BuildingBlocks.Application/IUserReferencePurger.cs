namespace BuildingBlocks.Application;

/// <summary>
/// Cómo un módulo borra lo que todavía guarda de un usuario que Identity está por borrar
/// físicamente. Es la otra mitad de <see cref="IUserReferenceProbe"/>: la sonda dice si el
/// usuario se retiene; el purgador limpia lo que, sin retenerlo, igual lo nombra (spec
/// 2026-10-02: la membresía quitada que bloqueaba su código de asesor para siempre).
/// </summary>
/// <remarks>
/// <para>Lo llama sólo <c>OrphanUserCleanupWorker</c>, sólo después de que <em>todas</em> las
/// sondas respondieron <c>false</c>, y mientras ese worker sostiene el advisory lock de ciclo de
/// vida del usuario (<see cref="UserLifecycleLockKey"/>).</para>
/// <para>La implementación <strong>no puede volver a tomar ese lock</strong>: corre en otra
/// conexión, así que esperaría para siempre a una transacción que no termina hasta que ella
/// vuelva, y Postgres no lo detecta como deadlock porque una de las dos esperas está en el
/// cliente y no en la base.</para>
/// <para>Commitea su propia unidad de trabajo: Identity no puede escribir en las tablas de otro
/// módulo, y las dos confirmaciones no son atómicas. Por eso tiene que ser idempotente —sin nada
/// que purgar, no hace nada—: si el borrado del usuario falla después de la purga, el worker
/// reintenta el mensaje entero cuando vence su espera y la purga vuelve a correr sobre lo que ya
/// borró. Una implementación que falla siempre no bloquea a nadie: el worker espera cada vez más
/// entre intentos —hasta 15 minutos—, lo loguea en Error desde el cuarto, y el mensaje se procesa
/// solo en el primer reintento después de que la falla se arregle.</para>
/// <para>Igual que la sonda, vive en BuildingBlocks para que Identity resuelva
/// <c>IEnumerable&lt;IUserReferencePurger&gt;</c> sin referenciar a ningún módulo de negocio. Un
/// módulo que no guarda nada que haya que borrar con el usuario simplemente no registra
/// purgador.</para>
/// </remarks>
public interface IUserReferencePurger
{
    /// <summary>Nombre del módulo que purga, igual que <see cref="IUserReferenceProbe.Source"/>.</summary>
    string Source { get; }

    /// <summary>Borra y commitea lo que el módulo guarda del usuario.</summary>
    /// <returns>
    /// Cuántas filas borró: <c>0</c> cuando no había nada, que es el caso del reintento. El worker
    /// lo escribe en su log, junto a <see cref="Source"/>, al borrar al usuario; el purgador no
    /// loguea nada por su cuenta.
    /// </returns>
    Task<int> PurgeAsync(Guid userId, CancellationToken cancellationToken);
}
