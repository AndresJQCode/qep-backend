using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// La segunda capa de la API externa (spec 2026-10-30 §4.3 y §5.2): la política del endpoint ya frenó a quien no trae el scope, y
/// cada handler lo revalida, igual que <c>MessagingAuthorization</c> con el permiso. Falla cerrado, y todo rechazo de conexión es 403,
/// nunca 404: un 404 confirmaría que el id existe en otro tenant.
/// </summary>
public static class ExternalApiAccess
{
    /// <summary>El scope y el módulo <c>messaging</c> del tenant de la clave. El tenant sin fila de módulos tampoco pasa.</summary>
    public static async Task EnsureAsync(
        IApiCaller caller, ITenantModules modules, string scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(modules);

        if (!caller.HasScope(scope))
        {
            throw new RequestForbiddenException(
                ExternalApiErrorCodes.ScopeMissing, "The API key does not have the required scope.");
        }

        await TenantModuleGuard.EnsureEnabledFailClosedAsync(
            modules, caller.TenantId, TenantModuleKeys.Messaging, cancellationToken);
    }

    /// <summary>
    /// La conexión tiene que estar permitida a la clave y ser del tenant de la clave (también con <c>connection_ids</c> nulo, que
    /// permite todas las del tenant y ninguna ajena). Una conexión de Instagram responde 422: las plantillas son de WhatsApp.
    /// </summary>
    public static async Task EnsureConnectionAsync(
        IApiCaller caller, IMessagingConnectionDirectory connections, Guid connectionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(connections);

        if (!caller.CanUseConnection(connectionId))
        {
            throw NotAllowed();
        }

        var channel = await connections.FindChannelAsync(caller.TenantId, connectionId, cancellationToken)
            ?? throw NotAllowed();
        TemplateOutbound.EnsureWhatsApp(channel);
    }

    private static RequestForbiddenException NotAllowed() =>
        new(ExternalApiErrorCodes.ConnectionNotAllowed, "The API key cannot use this connection.");
}
