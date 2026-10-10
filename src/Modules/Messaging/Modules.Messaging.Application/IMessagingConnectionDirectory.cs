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

    /// <summary>Nombre de cada conexión whatsapp-cloud del tenant, Active o no.</summary>
    Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary><c>Active → NeedsAttention</c> con el código (<c>token_expired</c>, <c>number_unregistered</c>, <c>account_disabled</c>).</summary>
    Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken);
}
