using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bootstrapper.ReverseProxy;

public static class ForwardedHeadersServiceCollectionExtensions
{
    /// <summary>
    /// Configura el middleware de encabezados reenviados para que <c>RemoteIpAddress</c> sea la IP
    /// del cliente detrás de ingress-nginx, tomada de <c>X-Real-IP</c>. El middleware lo agrega
    /// <c>UseForwardedHeaders</c> en Program.cs; esto decide qué encabezado se lee y en quién se
    /// confía.
    /// </summary>
    public static IServiceCollection AddQepForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ForwardedHeadersSettings>()
            .Bind(configuration.GetSection(ForwardedHeadersSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ForwardedHeadersSettings>, ForwardedHeadersSettingsValidator>();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersSettings>>((options, settings) =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                // La IP del cliente sale de X-Real-IP, no de X-Forwarded-For. Detrás de Cloudflare,
                // nginx manda X-Forwarded-For como "<lo que llegó>, <IP del borde de Cloudflare>": la
                // entrada de más a la derecha es el borde, no el cliente, y las de la izquierda las
                // escribe cualquiera. X-Real-IP es el $remote_addr de nginx, que sólo se resuelve
                // desde CF-Connecting-IP cuando el par es de Cloudflare (set_real_ip_from), así que
                // no se puede falsificar desde afuera. Con este nombre, X-Forwarded-For se ignora.
                options.ForwardedForHeaderName = "X-Real-IP";
                // X-Real-IP trae un solo valor. Si llegara una lista, sólo cuenta la última entrada.
                options.ForwardLimit = 1;
                // Se suman al loopback que el framework ya trae (127.0.0.0/8 y ::1), no lo reemplazan.
                // System.Net.IPNetwork y no el de HttpOverrides, que en .NET 10 está obsoleto.
                foreach (var network in settings.Value.KnownNetworks)
                {
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            });

        return services;
    }
}
