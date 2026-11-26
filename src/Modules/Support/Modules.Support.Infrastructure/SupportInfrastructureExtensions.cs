using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Support.Application;
using Modules.Support.Infrastructure.Legal;
using Modules.Support.Infrastructure.Pdf;
using Modules.Support.Infrastructure.Persistence;

namespace Modules.Support.Infrastructure;

public static class SupportInfrastructureExtensions
{
    public static IServiceCollection AddSupportInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<SupportDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    SupportDbContext.Schema)));

        services.AddScoped<ISupportUnitOfWork, SupportUnitOfWork>();
        services.AddScoped<ISupportProvisioner, SupportProvisioner>();
        services.AddScoped<ISupportTicketNumberGenerator, SupportTicketNumberGenerator>();
        services.AddScoped<ISupportSettingsRepository, SupportSettingsRepository>();
        services.AddScoped<ISupportTicketTypeRepository, SupportTicketTypeRepository>();
        services.AddScoped<ISupportTicketRepository, SupportTicketRepository>();
        services.AddScoped<ISupportTicketMessageRepository, SupportTicketMessageRepository>();
        services.AddScoped<ISupportUnclassifiedRepository, SupportUnclassifiedRepository>();
        services.AddScoped<ISupportReplyRepository, SupportReplyRepository>();
        services.AddScoped<ISupportAuditPublisher, SupportAuditPublisher>();
        // Spec 2026-11-22 §12 (and the internal-tasks spec §6 D-3): the integration events go to the platform outbox in the same unit of
        // work as the ticket change.
        services.AddScoped<ISupportIntegrationEvents, SupportIntegrationEvents>();

        // Fase 3, formulario público (plan F7, F8, F14, F16, F17).
        services.AddScoped<ISupportTicketAccessRepository, SupportTicketAccessRepository>();
        services.AddScoped<ISupportFormRepository, SupportFormRepository>();
        services.AddSingleton<ISupportPinHasher, Pbkdf2SupportPinHasher>();
        // Spec 2026-11-15 §6.3 (plan L2): HtmlSupportSanitizer is stateless, it builds its HtmlSanitizer on each call; the decorator caches
        // its output by the SHA-256 of the input (final review S1), bounded and per pod (MultiReplicaInventory).
        services.AddSingleton<ISupportHtmlSanitizer>(_ => new CachingSupportHtmlSanitizer(new HtmlSupportSanitizer()));
        services.AddScoped<ISupportLegalDocumentRepository, SupportLegalDocumentRepository>();
        services.AddScoped<ISupportLegalAcceptanceRepository, SupportLegalAcceptanceRepository>();
        // Plan F35 y F36: los adjuntos del formulario, y la sonda que le dice al barrido de Storage cuáles ya reclamó un ticket.
        services.AddScoped<ISupportTicketAttachmentRepository, SupportTicketAttachmentRepository>();
        services.AddScoped<ISupportTicketNoteRepository, SupportTicketNoteRepository>();
        services.AddScoped<IFileReferenceProbe, SupportAttachmentReferenceProbe>();
        // Spec 2026-11-15 §5.2: the ticket notices of the app hub, after each commit.
        services.AddScoped<ISupportRealtime, SupportRealtimeNotifier>();

        // Fase 4b (H12): el PDF de la respuesta formal, con sección y clave propias de qcode-pdf. Sin la clave en Production el pod no
        // arranca (ValidateOnStart). Singleton por fábrica con un HttpClient propio y su timeout, como QCodePdfRenderer: sin estado.
        services.AddOptions<SupportPdfOptions>().Bind(configuration.GetSection(SupportPdfOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SupportPdfOptions>, SupportPdfOptionsValidator>();
        services.AddSingleton<ISupportFormalReplyRenderer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<SupportPdfOptions>>();
            return new SupportPdfRenderer(
                new HttpClient { Timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds) },
                options,
                provider.GetRequiredService<ILogger<SupportPdfRenderer>>());
        });

        // Fase 2 (plan E2): el seguimiento del hilo sondea los entrantes de Mensajería. Apagable por configuración para que las pruebas
        // lo llamen a mano sin que el worker se lleve los entrantes por detrás.
        services.AddScoped<SupportThreadFollower>();
        // Fase 4b (H16): la cola de la encuesta de satisfacción y quien la manda.
        services.AddScoped<ISupportSurveyRepository, SupportSurveyRepository>();
        services.AddScoped<ISupportReportRepository, SupportReportRepository>();
        services.AddScoped<SupportSystemAuthor>();
        services.AddScoped<SupportSurveySender>();
        services.AddScoped<SupportSurveyAnswers>();
        services.AddScoped<SupportAutoCloser>();
        // Spec 2026-11-22 §7 and D-16: the unassigned-ticket alert reads and claims through this store.
        services.AddScoped<SupportUnassignedAlertStore>();
        // Task-notices spec 2026-11-22 §5.7 and D-7: what the notice escalation re-checks before sending; reached through a Bootstrapper port.
        services.AddScoped<ISupportTaskNoticeReader, SupportTaskNoticeReader>();
        services.AddOptions<SupportWorkerOptions>().Bind(configuration.GetSection(SupportWorkerOptions.SectionName));
        if (configuration.GetValue($"{SupportWorkerOptions.SectionName}:Enabled", true))
        {
            services.AddHostedService<SupportThreadFollowWorker>();
            services.AddHostedService<SupportSurveyWorker>();
            services.AddHostedService<SupportAutoCloseWorker>();
            services.AddHostedService<SupportUnassignedAlertWorker>();
        }

        return services;
    }
}
