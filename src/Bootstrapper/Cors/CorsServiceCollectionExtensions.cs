using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bootstrapper.Cors;

public static class CorsServiceCollectionExtensions
{
    private const string PolicyName = "spa";

    /// <summary>
    /// La política del formulario público de Soporte (plan fase 3, F4). <c>PublicSupportEndpoints.CorsPolicyName</c> la duplica porque
    /// Bootstrapper no referencia los <c>.Api</c>: <c>SupportPublicPathsTests</c> las ata.
    /// </summary>
    public const string SupportPublicPolicyName = "support-public";

    /// <summary>
    /// Registra la política CORS de la SPA a partir de <c>Cors:AllowedOrigins</c>, y la del formulario público de Soporte a partir de
    /// <c>Support:PublicForm:AllowedOrigins</c>. El middleware lo agrega <see cref="UseQepCors"/> en Program.cs, y sólo si alguna de
    /// las dos listas trae algo.
    /// </summary>
    public static IServiceCollection AddQepCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(CorsSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CorsSettings>, CorsSettingsValidator>();

        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<CorsSettings>>((options, settings) =>
                options.AddPolicy(PolicyName, policy => policy
                    // Lista exacta, nunca SetIsOriginAllowed ni comodines: la defensa CSRF depende
                    // de que ningún otro origen pase el preflight (ver RequireCsrfHeaderMiddleware).
                    .WithOrigins([.. settings.Value.AllowedOrigins])
                    // La cookie de sesión. Sin esto el navegador no la manda ni deja leer la respuesta.
                    .AllowCredentials()
                    // Los cinco que la API mapea hoy: PATCH sólo lo usa la metadata de archivos.
                    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                    // Lo que manda el api-client de la SPA: Authorization lleva el ID token de Google
                    // en el login y el registro; If-Match la concurrencia optimista.
                    // X-Requested-With y X-SignalR-User-Agent los manda el cliente de SignalR en el negotiate del hub
                    // de Mensajería (spec 2026-10-14); sin ellos el preflight falla y el canal nunca abre.
                    .WithHeaders("Content-Type", "Authorization", "X-Qep-Client", "X-Tenant-Id", "If-Match", "X-Requested-With", "X-SignalR-User-Agent")
                    // Sin WithExposedHeaders: la SPA sólo lee Content-Type y Content-Length, que el
                    // navegador ya expone por estar en la lista segura de CORS.
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        // Formulario público de Soporte (plan F4): otra política con nombre, que sólo pide el grupo /api/v1/public/support/… con
        // RequireCors. El metadato del endpoint gana sobre la política por defecto del middleware, así que «spa» no se extiende al grupo
        // ni ésta a la consola. SIN credenciales, a propósito: el grupo es anónimo y la página no tiene sesión que mandar. Orígenes
        // exactos, validados como los de la SPA (SupportPublicFormOptionsValidator).
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<SupportPublicFormOptions>>((options, settings) =>
                options.AddPolicy(SupportPublicPolicyName, policy => policy
                    .WithOrigins([.. settings.Value.AllowedOrigins])
                    .WithMethods("GET", "POST")
                    .WithHeaders("Content-Type")
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }

    /// <summary>
    /// Agrega el middleware de CORS sólo si hay orígenes configurados en alguna de las dos listas. Con las dos vacías no se registra
    /// nada, a propósito: el middleware con una política sin orígenes no es neutro —contesta 204 a cualquier preflight en lugar del 405
    /// de hoy—, y sin orígenes no hay nada que permitir. Con orígenes sólo para Soporte, el middleware va sin política por defecto: la
    /// consola no recibe CORS y el grupo público usa la suya. Leer los valores acá también dispara los validadores antes del primer
    /// request.
    /// </summary>
    public static IApplicationBuilder UseQepCors(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<CorsSettings>>().Value;
        var support = app.ApplicationServices.GetRequiredService<IOptions<SupportPublicFormOptions>>().Value;
        if (settings.AllowedOrigins.Count > 0)
        {
            return app.UseCors(PolicyName);
        }

        return support.AllowedOrigins.Count > 0 ? app.UseCors() : app;
    }
}
