namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §8.7: <c>sentBy.displayName</c> de los mensajes de una página, en un lookup
/// (como Quotations con el asesor). Adaptador en Bootstrapper sobre Tenancy e Identity. La membresía que
/// ya no está (o sin nombre ni correo) no aparece en el diccionario, y <c>MessageMapping</c> la manda como
/// «Miembro eliminado» (D-M20): <c>displayName</c> nunca viaja <c>null</c>.</summary>
public interface IMessagingMemberNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}
