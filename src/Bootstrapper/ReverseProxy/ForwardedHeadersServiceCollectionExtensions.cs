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
    /// del cliente detrás de ingress-nginx. El middleware lo agrega <c>UseForwardedHeaders</c> en
    /// Program.cs; esto sólo decide en quién se confía.
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
                // Sólo la entrada de más a la derecha: es la que agrega nginx con la IP que resolvió
                // (compute-full-forwarded-for). Las de la izquierda las escribe el cliente y se
                // pueden falsificar; confiar en ellas le dejaría a cualquiera elegir su bucket.
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
