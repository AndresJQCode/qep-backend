using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.Infrastructure;

public static class MessagingInfrastructureExtensions
{
    public static IServiceCollection AddMessagingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<MessagingDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", MessagingDbContext.Schema)));

        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IMessagingUnitOfWork, MessagingUnitOfWork>();
        // §7.6 y §8.7: las lecturas de la bandeja y el armado de ConversationSummary por página.
        services.AddScoped<IConversationQueries, ConversationQueries>();
        services.AddScoped<IMessageQueries, MessageQueries>();
        services.AddScoped<ConversationSummaryBuilder>();
        // Spec 2026-10-09 §8.2: la firma del webhook y la cola deduplicada de entregas.
        services.AddSingleton<IWebhookSignatureVerifier, HmacWebhookSignatureVerifier>();
        services.AddScoped<IWebhookDeliveries, WebhookDeliveries>();
        // §8.2: rutas con caché por pod, procesador por entrega y el worker que reclama.
        services.AddMemoryCache();
        services.AddScoped<WebhookRouting>();
        services.AddScoped<WebhookDeliveryProcessor>();
        services.AddHostedService<WebhookDeliveryWorker>();
        // §8.2: purga diaria de las entregas procesadas.
        services.AddHostedService<WebhookPurgeWorker>();

        // Spec 2026-10-09 §9. Meta:App se valida en Integrations (P1); acá sólo se bindea lo que se usa.
        services.AddOptions<MessagingMetaOptions>().Bind(configuration.GetSection(MessagingMetaOptions.SectionName));
        services.AddOptions<MessagingWebhookOptions>()
            .Bind(configuration.GetSection(MessagingWebhookOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MessagingWebhookOptions>, MessagingWebhookOptionsValidator>();
        services.AddOptions<MessagingWorkerOptions>().Bind(configuration.GetSection(MessagingWorkerOptions.SectionName));

        return services;
    }
}
