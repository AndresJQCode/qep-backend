using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-30 §5.2: las rutas de WhatsApp de la API externa, bajo <c>/api/v1/external/whatsapp</c>. El grupo padre (en
/// <c>src/Api</c>) fija el esquema <c>ApiKey</c> y el filtro de la capacidad <c>developer</c>; cada ruta agrega aquí su scope, que es la
/// capa del endpoint, y su handler lo revalida (<see cref="ExternalApiAccess"/>).
/// </summary>
public static class ExternalWhatsAppEndpoints
{
    public static RouteGroupBuilder MapExternalWhatsAppEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var whatsapp = group.MapGroup("/whatsapp");

        whatsapp.MapGet("/connections", ListConnectionsAsync)
            .RequireAuthorization(RequireTemplatesSend)
            .Produces<IReadOnlyList<ExternalConnectionDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        whatsapp.MapGet("/connections/{connectionId:guid}/templates", ListTemplatesAsync)
            .RequireAuthorization(RequireTemplatesSend)
            .Produces<IReadOnlyList<ExternalTemplateDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return group;
    }

    private static void RequireTemplatesSend(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy) =>
        policy
            .AddAuthenticationSchemes(ApiKeyDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(ApiKeyClaimTypes.Scope, MessagingScopes.TemplatesSend);

    private static async Task<IResult> ListConnectionsAsync(IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListExternalConnectionsQuery(), cancellationToken));

    private static async Task<IResult> ListTemplatesAsync(
        Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListExternalTemplatesQuery(connectionId), cancellationToken));
}
