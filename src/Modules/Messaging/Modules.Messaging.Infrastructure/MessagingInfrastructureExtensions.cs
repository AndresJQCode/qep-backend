using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Campaigns;
using Modules.Messaging.Infrastructure.Media;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Messaging.Infrastructure.Realtime;
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
        services.AddScoped<IBotOutbound, BotOutbound>();
        // Spec 2026-10-21 D16: la plantilla de cotización al hilo del número, sin las guardas de la bandeja.
        services.AddScoped<IQuotationTemplateOutbound, QuotationTemplateOutbound>();
        // Spec 2026-10-11 decisión 9: abrir la conversación de un cliente desde la plataforma (SQL, como la ingesta).
        services.AddScoped<IConversationOpener, ConversationOpener>();
        services.AddScoped<IMessagingUnitOfWork, MessagingUnitOfWork>();
        // §8.5: auditoría atómica propia, en la misma transacción que el agregado (nunca el IAuditRecorder de Tenancy).
        services.AddScoped<IMessagingAuditRecorder, MessagingAuditRecorder>();
        // Spec 2026-10-10 §8.7: los eventos que escriben los handlers, en el mismo SaveChanges que el agregado.
        services.AddScoped<IConversationEvents, ConversationEvents>();
        services.AddScoped<IAutomationInbox, AutomationInbox>();
        // §7.6 y §8.7: las lecturas de la bandeja y el armado de ConversationSummary por página.
        services.AddScoped<IConversationQueries, ConversationQueries>();
        services.AddScoped<IMessageQueries, MessageQueries>();
        services.AddScoped<ConversationSummaryBuilder>();
        // Spec 2026-10-10 P20: la membresía de quien llama, una vez por request.
        services.AddScoped<CallerMembership>();
        // §7.3 y §8.8: la búsqueda full-text con statement_timeout acotado.
        services.AddScoped<IMessageSearch, MessageSearch>();
        // Spec 2026-10-14 (tiempo real), decisión 6: los avisos salen por pg_notify y cada pod los escucha y los
        // reparte a los IMessagingEventSink registrados (el del hub lo registra Modules.Messaging.Api).
        services.AddScoped<IMessagingNotifier, PgMessagingNotifier>();
        services.AddHostedService<MessagingEventsListener>();
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
        // Spec 2026-10-11 §6.4: la subida de la nota de voz, igual que el anterior pero con 30 s.
        services.AddHttpClient(WhatsAppCloudClient.UploadHttpClientName, WhatsAppCloudClient.ConfigureUploadClient)
            .ConfigurePrimaryHttpMessageHandler(() => WhatsAppCloudClient.CreatePrimaryHandler())
            .RemoveAllLoggers();
        // Messaging-media-reply: la subida de imágenes y documentos (hasta 100 MB), con 5 minutos.
        services.AddHttpClient(WhatsAppCloudClient.MediaHttpClientName, WhatsAppCloudClient.ConfigureMediaClient)
            .ConfigurePrimaryHttpMessageHandler(() => WhatsAppCloudClient.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddSingleton<IWhatsAppCloudClient, WhatsAppCloudClient>();
        // Spec 2026-10-14 §6.2: la Messaging API de Instagram (graph.instagram.com, 10 s) y la descarga de adjuntos entrantes desde el
        // CDN de Meta (con redirecciones, 5 min). Decisión 16: el transcodificador de notas de voz.
        services.AddHttpClient(InstagramClient.HttpClientName, InstagramClient.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => InstagramClient.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddHttpClient(InstagramClient.DownloadHttpClientName, InstagramClient.ConfigureDownloadClient)
            .ConfigurePrimaryHttpMessageHandler(() => InstagramClient.CreateDownloadHandler())
            .RemoveAllLoggers();
        services.AddSingleton<IInstagramClient, InstagramClient>();
        services.AddSingleton<IOpusTranscoder, ConcentusOpusTranscoder>();
        services.AddScoped<IOutboundMessages, OutboundMessages>();
        // Spec 2026-10-14 D-T8: el catálogo lee del espejo; el refresco es el mismo servicio que usa la pantalla de administración.
        services.AddScoped<TemplateMirrorSync>();
        services.AddScoped<TemplateManagementReader>();
        services.AddScoped<IMessageTemplates, MirroredTemplateCatalog>();
        // Spec 2026-10-11 §6: las campañas. El agregado con tracking, las lecturas con sus contadores y el armado de los DTO.
        services.AddScoped<ICampaignRepository, CampaignRepository>();
        services.AddScoped<ICampaignQueries, CampaignQueries>();
        services.AddScoped<CampaignMapper>();
        // Spec 2026-10-12 §3.1: las respuestas rápidas, con tracking; el atajo repetido lo traduce MessagingUnitOfWork.
        services.AddScoped<IQuickReplyRepository, QuickReplyRepository>();
        services.AddScoped<IResolutionReasonRepository, ResolutionReasonRepository>();
        services.AddScoped<IResolutionReasonNames, ResolutionReasonRepository>();
        // Spec 2026-10-21 §5: los horarios de atención; el repositorio es también el puerto IBusinessHours (misma instancia por scope).
        services.AddScoped<IBusinessHoursRepository, BusinessHoursRepository>();
        // Spec 2026-10-28: la extracción SQL del reporte de mensajería.
        services.AddScoped<IMessagingReportSource, MessagingReportSource>();
        // El cast es a propósito: las dos interfaces tienen que compartir la instancia scoped (un solo repositorio por solicitud).
        services.AddScoped<IBusinessHours>(provider => (BusinessHoursRepository)provider.GetRequiredService<IBusinessHoursRepository>());
        // Spec 2026-10-14 §4.3: el espejo de plantillas.
        services.AddScoped<IMessageTemplateRepository, MessageTemplateRepository>();
        // Spec 2026-10-14 §5: la señal que el consumidor Quotations leerá por un adaptador en Bootstrapper.
        services.AddScoped<IQuotationTemplateReadiness, QuotationTemplateReadiness>();
        // §8.6: la copia de medios entrantes a R2 (por IMessagingMediaStore) y su lectura para servirlos.
        services.AddScoped<IMediaReads, MediaReads>();
        services.AddScoped<MediaTransfer>();
        services.AddScoped<MediaCopyProcessor>();
        services.AddHostedService<MediaCopyWorker>();
        // Spec 2026-10-11 §6.3: el worker de campañas. El cubo de ritmo es un singleton (el estado es del proceso); el despachador y su
        // persistencia, scoped. TimeProvider.System sólo si nadie registró otro.
        services.TryAddSingleton(TimeProvider.System);
        // Decisión del owner 2026-10-12 (P3 y P5): el límite de envío es un cubo por conexión en Postgres, compartido por las réplicas.
        services.AddScoped<IConnectionSendBudget, ConnectionSendBudget>();
        services.AddScoped<ICampaignDispatchStore, CampaignDispatchStore>();
        services.AddScoped<CampaignDispatcher>();
        services.AddHostedService<CampaignDispatchWorker>();
        // Spec 2026-10-29: el evento de coexistencia crea la importación de historial Pending. El procesador es singleton (el estado es del proceso).
        services.AddSingleton<Coexistence.CoexistenceEventsProcessor>();
        services.AddHostedService<Coexistence.CoexistenceEventsWorker>();
        // Spec 2026-10-29: el worker que le pide a Meta los contactos y el historial de cada importación Pending.
        services.TryAddSingleton(Coexistence.HistorySyncRequestSchedule.Default);
        services.AddSingleton<Coexistence.HistorySyncRequestProcessor>();
        services.AddHostedService<Coexistence.HistorySyncRequestWorker>();

        // Spec 2026-10-09 §9. Meta:App se valida en Integrations (P1); acá sólo se bindea lo que se usa.
        services.AddOptions<MessagingMetaOptions>().Bind(configuration.GetSection(MessagingMetaOptions.SectionName));
        // Spec 2026-10-14 §9: Meta:Instagram se valida en Integrations; acá sólo el secreto del webhook y la versión.
        services.AddOptions<MessagingInstagramOptions>().Bind(configuration.GetSection(MessagingInstagramOptions.SectionName));
        services.AddOptions<MessagingWebhookOptions>()
            .Bind(configuration.GetSection(MessagingWebhookOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MessagingWebhookOptions>, MessagingWebhookOptionsValidator>();
        services.AddOptions<MessagingCampaignOptions>()
            .Bind(configuration.GetSection(MessagingCampaignOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MessagingCampaignOptions>, MessagingCampaignOptionsValidator>();
        services.AddOptions<MessagingMarketingOptOutOptions>().Bind(configuration.GetSection(MessagingMarketingOptOutOptions.SectionName));
        services.AddSingleton<MarketingOptOutButtons>();
        services.AddOptions<MessagingWorkerOptions>().Bind(configuration.GetSection(MessagingWorkerOptions.SectionName));
        services.AddOptions<MessagingAutomationOptions>().Bind(configuration.GetSection(MessagingAutomationOptions.Section));
        services.AddOptions<MessagingSearchOptions>().Bind(configuration.GetSection(MessagingSearchOptions.SectionName));

        return services;
    }
}
