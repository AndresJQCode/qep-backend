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
    /// Registra la política CORS de la SPA a partir de <c>Cors:AllowedOrigins</c>. El middleware lo
    /// agrega <see cref="UseQepCors"/> en Program.cs, y sólo si la lista trae algo.
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
                    .WithHeaders("Content-Type", "Authorization", "X-Qep-Client", "X-Tenant-Id", "If-Match")
                    // Sin WithExposedHeaders: la SPA sólo lee Content-Type y Content-Length, que el
                    // navegador ya expone por estar en la lista segura de CORS.
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }

    /// <summary>
    /// Agrega el middleware de CORS sólo si hay orígenes configurados. Con la lista vacía no se
    /// registra nada, a propósito: el middleware con una política sin orígenes no es neutro —
    /// contesta 204 a cualquier preflight en lugar del 405 de hoy—, y sin orígenes no hay nada que
    /// permitir. Leer el valor acá también dispara el validador antes del primer request.
    /// </summary>
    public static IApplicationBuilder UseQepCors(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<CorsSettings>>().Value;
        return settings.AllowedOrigins.Count > 0
            ? app.UseCors(PolicyName)
            : app;
    }
}
