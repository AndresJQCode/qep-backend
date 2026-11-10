using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Store.Application;
using Modules.Store.Infrastructure.Captcha;
using Modules.Store.Infrastructure.Persistence;

namespace Modules.Store.Infrastructure;

public static class StoreInfrastructureExtensions
{
    public static IServiceCollection AddStoreInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<StoreDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    StoreDbContext.Schema)));

        services.AddScoped<IOnlineStoreRepository, OnlineStoreRepository>();
        services.AddScoped<IStoreDomainRepository, StoreDomainRepository>();
        services.AddScoped<IStoreOrderRequestRepository, StoreOrderRequestRepository>();
        services.AddScoped<IStoreOrderConsents, StoreOrderConsentRepository>();
        services.AddScoped<IStoreRequestNumberGenerator, StoreRequestNumberGenerator>();
        services.AddScoped<IStoreUnitOfWork, StoreUnitOfWork>();
        services.AddScoped<IStoreAuditPublisher, StoreAuditPublisher>();
        services.AddScoped<IStoreOutbox, StoreOutboxWriter>();

        // Secretos y topes de la tienda: validados al arrancar (los secretos son obligatorios sólo en Production).
        services.AddOptions<StoreOptions>().Bind(configuration.GetSection(StoreOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<StoreOptions>, StoreOptionsValidator>();
        // Los manejadores piden el valor ya enlazado, no IOptions: Application no referencia Microsoft.Extensions.Options.
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<StoreOptions>>().Value);

        // Turnstile: un cliente propio con 5 s, sin redirecciones y sin los loggers por defecto. Enabled=false (sólo Development y
        // Testing; el arranque lo prohíbe en cualquier otro ambiente, Staging incluido) registra el verificador que siempre pasa.
        if (configuration.GetValue("Store:Turnstile:Enabled", true))
        {
            services.AddHttpClient(CloudflareTurnstileVerifier.HttpClientName, CloudflareTurnstileVerifier.ConfigureClient)
                .ConfigurePrimaryHttpMessageHandler(() => CloudflareTurnstileVerifier.CreatePrimaryHandler())
                .RemoveAllLoggers();
            services.AddScoped<ICaptchaVerifier>(provider => new CloudflareTurnstileVerifier(
                provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetRequiredService<StoreOptions>(),
                // El hostname que reporta Cloudflare es el del sitio donde se pintó el widget: con las llaves de prueba nunca es el de
                // la tienda, así que sólo se exige en Production.
                provider.GetRequiredService<IHostEnvironment>().IsProduction(),
                provider.GetRequiredService<ILogger<CloudflareTurnstileVerifier>>()));
        }
        else
        {
            services.AddSingleton<ICaptchaVerifier, PassThroughCaptchaVerifier>();
        }

        return services;
    }
}
