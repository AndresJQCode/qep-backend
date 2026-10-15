namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 (Mensajería en tiempo real), decisión 2: el canal avisa, no transporta. Lo que cambió,
/// por ids, para que la SPA invalide sus consultas y vuelva a pedir por HTTP; nunca viaja un mensaje ni una
/// conversación, y la autorización sigue en los <c>GET</c>. Se llama <b>después</b> del commit: un aviso de
/// algo que luego se deshace mandaría a la pantalla a leer lo que no existe. La implementación reparte entre
/// pods (<c>NOTIFY/LISTEN</c>, decisión 6) y un fallo suyo no deshace nada: el polling relajado del
/// frontend cubre el hueco.
/// </summary>
public interface IMessagingNotifier
{
    /// <summary>Asignación, resolver, reabrir, leído, no leído, saliente consolidado o acuse aplicado.</summary>
    Task ConversationChangedAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Un entrante insertado (no un reenvío de Meta). <paramref name="assignedMemberId"/> decide a quién le
    /// suena (decisión 4): a ese miembro, o a todo el tenant si la conversación no tiene asignado.</summary>
    Task InboundMessageAsync(Guid tenantId, Guid conversationId, Guid messageId, Guid? assignedMemberId, CancellationToken cancellationToken);
}

/// <summary>El sobre que viaja entre pods y llega al hub. <see cref="Kind"/> ∈ <see cref="MessagingEventKinds"/>.
/// Se serializa en camelCase (<c>JsonSerializerDefaults.Web</c>): es el mismo nombre que el evento del cliente.</summary>
public sealed record MessagingEvent(string Kind, Guid TenantId, Guid ConversationId, Guid? MessageId, Guid? AssignedMemberId);

public static class MessagingEventKinds
{
    public const string ConversationChanged = "conversationChanged";

    public const string InboundMessage = "inboundMessage";
}

/// <summary>
/// Quien entrega un <see cref="MessagingEvent"/> a los clientes conectados a <b>este</b> pod. Lo implementa
/// <c>Modules.Messaging.Api</c> sobre <c>IHubContext</c>; Infrastructure sólo lo llama desde el listener de
/// <c>NOTIFY</c>. Está en Application y no en Api porque Infrastructure no referencia Api.
/// </summary>
public interface IMessagingEventSink
{
    Task DispatchAsync(MessagingEvent notification, CancellationToken cancellationToken);
}
