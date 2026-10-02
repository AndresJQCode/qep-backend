using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Audit.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Borra físicamente las membresías quitadas o vencidas de un usuario que Identity está por borrar
/// por no tener historia (spec 2026-10-02). Sin esto la fila quedaba apuntando a un usuario
/// inexistente y su código de asesor seguía bloqueado para siempre, porque la unicidad cuenta a
/// las quitadas (D4 del spec 2026-09-24).
/// </summary>
/// <remarks>
/// <para>Lo llama <c>OrphanUserCleanupWorker</c> sólo después de que
/// <see cref="MembershipUserReferenceProbe"/> —y todas las demás sondas— respondió que el usuario
/// no se retiene, y con el lock de ciclo de vida del usuario tomado por el worker. Por eso guarda
/// con <see cref="ITenancyUnitOfWork.SaveChangesAsync"/> y nunca con
/// <see cref="ITenancyUnitOfWork.BeginUserLifecycleScopeAsync"/>: volver a pedir ese lock desde
/// otra conexión esperaría para siempre (ver <see cref="IUserReferencePurger"/>).</para>
/// <para>No emite eventos de outbox: nadie consume un borrado de membresía, y quitarla ya emitió
/// el suyo. Lo que queda de la fila es la auditoría, una entrada por membresía.</para>
/// </remarks>
public sealed class MembershipUserReferencePurger(
    IMembershipRepository membershipRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IClock clock)
    : IUserReferencePurger
{
    public string Source => "tenancy";

    public async Task PurgeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var memberships = await membershipRepository.ListByUserAsync(userId, cancellationToken);
        // Idempotente: en el reintento después de una falla a mitad de camino ya no hay nada.
        if (memberships.Count == 0)
        {
            return;
        }

        // Defensivo: la sonda ya garantizó que no hay vivas, pero si aparece una se corta antes
        // de borrar ninguna. Todas se revisan primero para que el rechazo no deje la mitad marcada.
        foreach (var membership in memberships)
        {
            membership.EnsurePurgeable();
        }

        var now = clock.UtcNow;
        foreach (var membership in memberships)
        {
            membershipRepository.Remove(membership);
            // Actor de sistema con el id del usuario afectado, igual que identity.user.deleted en
            // el worker: no hay una persona detrás de esta escritura.
            auditRecorder.Record(
                membership.TenantId.Value,
                userId,
                "tenancy.membership.purged",
                "membership",
                membership.Id.ToString(),
                "success",
                [],
                now,
                AuditActorType.System);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
