using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application;
using Modules.Automations.Application.Http;
using Modules.Automations.Infrastructure.Engine;
using Modules.Automations.Infrastructure.Http;
using Modules.Automations.Infrastructure.Options;
using Modules.Automations.Infrastructure.Persistence;
using Modules.Automations.Infrastructure.Seed;

namespace Modules.Automations.Infrastructure;

public static class AutomationsInfrastructureExtensions
{
    public static IServiceCollection AddAutomationsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<AutomationsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AutomationsDbContext.Schema)));

        // La membresía de quien llama, una vez por request: autor del borrador, de la publicación y de la auditoría.
        services.AddScoped<AutomationsCaller>();
        // El editor y la lista (BFF, spec 2026-10-21 §4.4): una sola pieza arma lo que la pantalla pinta.
        services.AddScoped<ChatbotEditorBuilder>();

        // D-C8: las plantillas del sistema se leen una vez de los recursos embebidos y no cambian mientras corre el proceso.
        services.AddSingleton<ISystemTemplateCatalog, SystemTemplateCatalog>();

        services.AddScoped<IChatbotRepository, ChatbotRepository>();
        services.AddScoped<IChatbotTemplateRepository, ChatbotTemplateRepository>();
        services.AddScoped<IConversationRunQueries, ConversationRunQueries>();
        services.AddScoped<IConversationRunRepository, ConversationRunRepository>();
        services.AddScoped<IAutomationsUnitOfWork, AutomationsUnitOfWork>();
        // Auditoría atómica: la entrada viaja en el mismo SaveChanges del cambio (ADR 0019).
        services.AddScoped<IAutomationsAuditRecorder, AutomationsAuditRecorder>();
        services.AddScoped<IAutomationsAuditQueries, AutomationsAuditQueries>();

        // Spec 2026-10-21 D-H4..D-H8: el bloque HTTP y su registro en http_calls. El cliente nombrado (HttpBlockExecutor.HttpClientName) se
        // registra en el composition root: su handler anti-SSRF es de Integrations.Infrastructure, que este módulo no referencia.
        services.AddScoped<IHttpCallLog, HttpCallLog>();
        services.AddScoped<IHttpBlockExecutor, HttpBlockExecutor>();

        // Spec 2026-10-21 §5.2: el motor atiende el inbox que llena la ingesta de Mensajería. RunProcessor y sus puertos se registran en
        // el composition root, con los adaptadores.
        services.AddOptions<AutomationsWorkerOptions>().Bind(configuration.GetSection(AutomationsWorkerOptions.SectionName));
        services.AddHostedService<AutomationInboxWorker>();
        return services;
    }
}
