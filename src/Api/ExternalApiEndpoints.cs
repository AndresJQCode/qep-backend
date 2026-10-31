using Api;
using Bootstrapper.ApiKeys;
using BuildingBlocks.Application;
using Modules.Messaging.Api;

/// <summary>
/// El grupo <c>/api/v1/external</c> (spec 2026-10-30 §5.2): lo que un sistema externo llama con <c>X-Api-Key</c>. Fija el esquema
/// <c>ApiKey</c> y nada más, de modo que la cookie de sesión no entra aquí; la capacidad <c>developer</c> se comprueba fallando
/// cerrado en el filtro. Cada módulo cuelga sus rutas de este grupo.
/// </summary>
internal static class ExternalApiEndpoints
{
    public static IEndpointRouteBuilder MapExternalApi(this IEndpointRouteBuilder app)
    {
        MapExternalGroup(app).MapExternalWhatsAppEndpoints();
        return app;
    }

    /// <summary>
    /// El grupo con toda su tubería: esquema, límite por IP, límite por clave y capacidad <c>developer</c>, en ese orden. Aparte
    /// de <see cref="MapExternalApi"/> para que las pruebas cuelguen una sonda del grupo real y no de una copia.
    /// </summary>
    internal static RouteGroupBuilder MapExternalGroup(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.MapGroup("/api/v1/external")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(ApiKeyDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser())
            .RequireRateLimiting(RateLimiterPolicies.External)
            .AddEndpointFilter<ApiKeyRateLimitFilter>()
            .AddEndpointFilter<DeveloperModuleFilter>()
            .WithTags("External API");
    }
}
