using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Media;
using Modules.Messaging.Infrastructure.Meta;
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
        // §8.5: auditoría atómica propia, en la misma transacción que el agregado (nunca el IAuditRecorder de Tenancy).
        services.AddScoped<IMessagingAuditRecorder, MessagingAuditRecorder>();
        // §7.6 y §8.7: las lecturas de la bandeja y el armado de ConversationSummary por página.
        services.AddScoped<IConversationQueries, ConversationQueries>();
        services.AddScoped<IMessageQueries, MessageQueries>();
        services.AddScoped<ConversationSummaryBuilder>();
        // Spec 2026-10-10 P20: la membresía de quien llama, una vez por request.
        services.AddScoped<CallerMembership>();
        // §7.3 y §8.8: la búsqueda full-text con statement_timeout acotado.
        services.AddScoped<IMessageSearch, MessageSearch>();
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
        // Decisión 3: el cliente de Graph de Messaging, mismo patrón que el de Integrations (sin redirecciones,
        // 10 s, sin los logs de HttpClient, que escribirían la URL). §8.3: el reclamo idempotente del envío.
        services.AddHttpClient(WhatsAppCloudClient.HttpClientName, WhatsAppCloudClient.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => WhatsAppCloudClient.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddSingleton<IWhatsAppCloudClient, WhatsAppCloudClient>();
        services.AddScoped<IOutboundMessages, OutboundMessages>();
        // §8.6: la copia de medios entrantes a R2 (por IMessagingMediaStore) y su lectura para servirlos.
        services.AddScoped<IMediaReads, MediaReads>();
        services.AddScoped<MediaTransfer>();
        services.AddScoped<MediaCopyProcessor>();
        services.AddHostedService<MediaCopyWorker>();

        // Spec 2026-10-09 §9. Meta:App se valida en Integrations (P1); acá sólo se bindea lo que se usa.
        services.AddOptions<MessagingMetaOptions>().Bind(configuration.GetSection(MessagingMetaOptions.SectionName));
        services.AddOptions<MessagingWebhookOptions>()
            .Bind(configuration.GetSection(MessagingWebhookOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MessagingWebhookOptions>, MessagingWebhookOptionsValidator>();
        services.AddOptions<MessagingWorkerOptions>().Bind(configuration.GetSection(MessagingWorkerOptions.SectionName));
        services.AddOptions<MessagingSearchOptions>().Bind(configuration.GetSection(MessagingSearchOptions.SectionName));

        return services;
    }
}
