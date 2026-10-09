using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.SecretProtection;
using Modules.Integrations.Infrastructure.Verification;
using Modules.Integrations.Infrastructure.Zenvia;

namespace Modules.Integrations.Infrastructure;

public static class IntegrationsInfrastructureExtensions
{
    public static IServiceCollection AddIntegrationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar en
        // el primer request con un error de Npgsql que no explica nada.
        _ = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        // Spec 2026-10-08, «Secreto en reposo»: en Production ValidateOnStart exige la llave activa
        // (sin ella el pod entra en crash-loop, a propósito); fuera de producción el host arranca y
        // crear o editar responde 503.
        services.AddOptions<SecretProtectionOptions>()
            .Bind(configuration.GetSection(SecretProtectionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecretProtectionOptions>, SecretProtectionOptionsValidator>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        // Spec 2026-10-08, «Probar la credencial». IHttpClientFactory con un cliente propio del
        // módulo; sin redirecciones automáticas (el token no viaja a otro host: un 3xx es «no pude
        // verificar», P13) y sin los loggers por defecto, que pueden registrar headers.
        services.AddOptions<ZenviaOptions>()
            .Bind(configuration.GetSection(ZenviaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ZenviaOptions>, ZenviaOptionsValidator>();
        services.AddHttpClient(ZenviaConnectionTester.HttpClientName, ZenviaConnectionTester.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<IProviderConnectionTester, ZenviaConnectionTester>();
        services.AddSingleton<IConnectionTester, ConnectionTesterRegistry>();

        return services;
    }
}
