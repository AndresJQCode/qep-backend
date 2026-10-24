using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Una ruta resuelta cross-tenant (spec §6.4): tenant, conexión y su estado por nombre
/// (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>). El texto y no un enum: Messaging no referencia Integrations.</summary>
public sealed record MessagingRoute(Guid TenantId, Guid ConnectionId, string Status)
{
    public bool IsActive => Status == "Active";

    public bool IsPaused => Status == "Paused";
}

/// <summary>Con qué se envía: el <c>phone_number_id</c> y el token en claro para ese request. <see cref="ToString"/> no lo imprime.</summary>
public sealed record MessagingSender(string PhoneNumberId, string AccessToken)
{
    public override string ToString() => $"MessagingSender {{ PhoneNumberId = {PhoneNumberId} }}";
}

/// <summary>Spec 2026-10-24 D-T11: el remitente o, si no se puede usar, por qué. <see cref="UnavailableReason"/> es un código
/// <c>snake_case</c> de spec §3.4 (<c>paused</c>, <c>secret_unreadable</c>, <c>wrong_provider</c>, <c>incomplete</c>, …), <c>null</c> si hay
/// remitente; <see cref="ConnectionName"/>, el nombre de la conexión cuando existe, también si no se puede usar.</summary>
public sealed record MessagingSenderResolution(MessagingSender? Sender, string? UnavailableReason, string? ConnectionName);

/// <summary>Spec 2026-10-14 §6.2: con qué se envía por Instagram: el id de la cuenta profesional y el token en claro para ese
/// request. <see cref="ToString"/> no lo imprime.</summary>
public sealed record InstagramSender(string IgUserId, string AccessToken)
{
    public override string ToString() => $"InstagramSender {{ IgUserId = {IgUserId} }}";
}

/// <summary>Spec 2026-10-21 D-H10: una conexión del tenant en cualquier estado. <c>Status</c> es el nombre del estado de Integrations
/// (<c>Active</c>, <c>Paused</c>, …): Messaging no referencia Integrations.</summary>
public sealed record MessagingConnectionRef(Guid Id, string Name, ConversationChannel Channel, string Status);

/// <summary>
/// Spec 2026-10-09 §6.4: lo que Messaging necesita de Integrations, con adaptador en Bootstrapper sobre
/// <c>IConnectionRoutes</c>, <c>IIntegrationConnections</c> e <c>IConnectionHealthReporter</c>. La caché
/// de 60 s de las rutas vive del lado de Messaging (<c>WebhookRouting</c>).
/// </summary>
public interface IMessagingConnectionDirectory
{
    /// <summary>Cross-tenant: el webhook no tiene tenant hasta acá. <c>null</c> = número desconocido o borrado.</summary>
    Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken);

    /// <summary>Las conexiones de una WABA (<c>account_update</c> llega por cuenta, D-M2).</summary>
    Task<IReadOnlyList<MessagingRoute>> FindByAccountAsync(string wabaId, CancellationToken cancellationToken);

    /// <summary>Sólo <c>Active</c> y visible, con el token en claro; <c>null</c> si no.</summary>
    Task<MessagingSender?> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-24 D-T11: lo mismo que <see cref="ResolveSenderAsync"/>, con el motivo cuando no hay remitente. Sólo lo usa
    /// el envío del bot (<c>BotOutbound</c>), que explica el traspaso; los demás envíos no necesitan la causa.</summary>
    Task<MessagingSenderResolution> ResolveSenderWithReasonAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Nombre de cada conexión whatsapp-cloud del tenant, Active o no.</summary>
    Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Todas las conexiones del tenant, en cualquier estado (D-H10): la página de horarios lista y valida contra esto, no contra
    /// <see cref="ListActiveAsync"/>. Una conexión borrada en Integrations nunca aparece (allá el borrado es físico): para los horarios
    /// es una asignación huérfana, no una opción. WhatsApp primero y después Instagram, cada grupo por nombre.</summary>
    Task<IReadOnlyList<MessagingConnectionRef>> ListAllAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §5.1: <c>wabaId</c> (<c>WhatsAppCloudFieldKeys.WabaId</c>) y el token en claro de una conexión
    /// <c>Active</c> del tenant; <c>null</c> si no existe, es de otro tenant, no está activa o no tiene <c>wabaId</c>.</summary>
    Task<MessagingTemplateSource?> ResolveTemplateSourceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §5.2: las conexiones <c>whatsapp-cloud</c> <c>Active</c> del tenant, por nombre, con el número
    /// que la pantalla muestra. Lo usan la conversación nueva y las campañas.</summary>
    Task<IReadOnlyList<MessagingConnectionDto>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary><c>Active → NeedsAttention</c> con el código (<c>token_expired</c>, <c>number_unregistered</c>, <c>account_disabled</c>).</summary>
    Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-14 §8.2: la ruta de una cuenta de Instagram (<c>entry[].id</c>). Cross-tenant, como
    /// <see cref="FindRouteAsync"/>. <c>null</c> = cuenta desconocida o conexión borrada.</summary>
    Task<MessagingRoute?> FindInstagramRouteAsync(string igUserId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-14 §6.2: sólo una conexión <c>instagram</c> <c>Active</c> y visible, con el token en claro; <c>null</c> si no.</summary>
    Task<InstagramSender?> ResolveInstagramSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-14 §5.2: el canal de una conexión del tenant, Active o no; <c>null</c> si no existe o es de otro tenant.
    /// Lo usan las guardas de plantillas y de conversación nueva, que reciben una conexión y no una conversación.</summary>
    Task<ConversationChannel?> FindChannelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}
