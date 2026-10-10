namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §8.7: <c>sentBy.displayName</c> de los mensajes de una página, en un lookup
/// (como Quotations con el asesor). Adaptador en Bootstrapper sobre Tenancy e Identity. La membresía que
/// ya no está no aparece y la pantalla recibe <c>displayName: null</c>.</summary>
public interface IMessagingMemberNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}
