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
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/external")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(ApiKeyDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser())
            .AddEndpointFilter<DeveloperModuleFilter>()
            .WithTags("External API");

        group.MapExternalWhatsAppEndpoints();
        return app;
    }
}
