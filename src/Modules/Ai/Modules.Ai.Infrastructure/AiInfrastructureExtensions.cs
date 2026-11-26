using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Ai.Infrastructure.Model;
using Modules.Ai.Infrastructure.Model.Anthropic;
using Modules.Ai.Infrastructure.Model.OpenAi;
using Modules.Ai.Infrastructure.Options;
using Modules.Ai.Infrastructure.Persistence;
using Modules.Ai.Infrastructure.Seed;

namespace Modules.Ai.Infrastructure;

public static class AiInfrastructureExtensions
{
    public static IServiceCollection AddAiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Plan 2026-11-12, C-3: la lista curada por proveedor. Rota, el pod no arranca: un agente no podría publicarse.
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IAiModelCatalog, ConfiguredAiModelCatalog>();

        // Spec 2026-11-23: las plantillas de agente del sistema se leen una vez de los recursos embebidos y no cambian mientras corre el proceso.
        services.AddSingleton<ISystemAgentTemplateCatalog, SystemAgentTemplateCatalog>();

        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");
        services.AddDbContext<AiDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AiDbContext.Schema)));
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IAiUnitOfWork, AiUnitOfWork>();
        services.AddScoped<IAiAuditRecorder, AiAuditRecorder>();
        // La membresía de quien llama, una vez por request: autor del borrador y de la publicación.
        services.AddScoped<AiCaller>();
        services.AddScoped<AgentEditorBuilder>();

        // Plan 2026-11-12, 5.2: un adaptador del modelo por proveedor, por clave; un proveedor sin el suyo no deja arrancar.
        services.AddScoped<IAiModelClientResolver, AiModelClientResolver>();
        services.AddSingleton<IValidateOptions<AiModelCallOptions>, AiModelCallOptionsValidator>();
        services.AddOptions<AiModelCallOptions>().Bind(configuration.GetSection(AiModelCallOptions.SectionName)).ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AiModelCallOptions>>().Value.ToLimits());
        // C-14 y D-A29: sin redirecciones y sin los loggers de HttpClient, que escribirían los headers (la clave). El timeout lo pone el
        // SDK por llamada (C-13); el del HttpClient queda infinito para que no corte antes.
        services.AddHttpClient(ClaudeModelClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddKeyedScoped<IAiModelClient, ClaudeModelClient>(AiProviders.Anthropic.Value);
        // Spec 2026-10-29 §9.1, O-2: lo mismo para OpenAI, que va con HttpClient directo; el timeout lo pone el adaptador por llamada.
        services.AddHttpClient(OpenAiModelClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddKeyedScoped<IAiModelClient, OpenAiModelClient>(AiProviders.OpenAi.Value);
        services.AddSingleton<IValidateOptions<AiModelClientRegistrationOptions>, AiModelClientRegistrationValidator>();
        services.AddOptions<AiModelClientRegistrationOptions>().ValidateOnStart();

        // Plan 2026-11-12, 5.5: el bucle común, sus herramientas de lectura (los puertos van en Bootstrapper) y lo que deja escrito.
        services.AddScoped<AgentPromptBuilder>();
        services.AddScoped<AgentTurnRunner>();
        services.AddScoped<IAgentTool, SearchCatalogTool>();
        services.AddScoped<IAgentTool, GetOrderStatusTool>();
        services.AddScoped<IAgentTool, SimulatedQuotationDraftTool>();
        // Spec 2026-10-29 §8.2 y P-4: la real, sólo en canales reales (sus puertos van en Bootstrapper).
        services.AddScoped<IAgentTool, CreateQuotationDraftTool>();
        // Spec 2026-11-23 §5: las seis de la Agenda y sus tres gemelas simuladas del playground (el puerto va en Bootstrapper).
        services.AddScoped<IAgentTool, ListBookableServicesTool>();
        services.AddScoped<IAgentTool, CheckAvailabilityTool>();
        services.AddScoped<IAgentTool, ListMyAppointmentsTool>();
        services.AddScoped<IAgentTool, BookAppointmentTool>();
        services.AddScoped<IAgentTool, CancelAppointmentTool>();
        services.AddScoped<IAgentTool, RescheduleAppointmentTool>();
        services.AddScoped<IAgentTool, SimulatedBookAppointmentTool>();
        services.AddScoped<IAgentTool, SimulatedCancelAppointmentTool>();
        services.AddScoped<IAgentTool, SimulatedRescheduleAppointmentTool>();
        services.AddScoped<IAiRunLedger, AiRunLedger>();
        // Plan 2026-11-12 Tarea 9: la actividad, el historial y los conteos de la lista.
        services.AddScoped<IAgentActivityReader, AgentActivityReader>();

        // Spec 2026-10-29 §7: el playground, con la cola ai.model_jobs y su worker.
        services.AddScoped<IAgentTestRepository, AgentTestRepository>();
        services.AddScoped<IAiModelJobs, AiModelJobs>();
        services.AddScoped<AgentTestJobProcessor>();
        services.AddOptions<AiWorkerOptions>().Bind(configuration.GetSection(AiWorkerOptions.SectionName));
        services.AddSingleton<AiModelWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<AiModelWorker>());

        // Spec 2026-10-29 §6.2–§6.8 (plan 2026-11-12, Tarea 7): las sesiones en canales reales. Los puertos hacia Mensajería (IAiInbox,
        // IAiConversations, IAiBotOutbound, IAiBusinessHours) van en Bootstrapper.
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AiWorkerOptions>>().Value.ToTimings());
        services.AddSingleton<AiMetrics>();
        services.AddScoped<IAgentSessionRepository, AgentSessionRepository>();
        services.AddScoped<IAiSessionUnitOfWork, AiSessionUnitOfWork>();
        services.AddScoped<IAiModuleStatus, AiModuleStatus>();
        services.AddScoped<AiInboxProcessor>();
        // Spec 2026-11-17 (copiloto): configuración e hilos.
        services.AddScoped<ICopilotRepository, CopilotRepository>();
        // D-C11: los topes por miembro, de Ai:Copilot (validados con AiOptions al arrancar).
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AiOptions>>().Value.Copilot.ToLimits());
        services.AddScoped<CopilotAsking>();
        // Plan fase 2, C2.3: el trabajo del copiloto en el mismo worker de modelo, con sus topes de Ai:Copilot. Su ejecutor de herramientas
        // (ICopilotToolExecutor) va en Bootstrapper: ejecuta consultas de otros módulos como el miembro.
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AiOptions>>().Value.Copilot.ToRunLimits());
        services.AddScoped<CopilotPromptBuilder>();
        services.AddScoped<CopilotTurnRunner>();
        services.AddScoped<CopilotJobProcessor>();
        services.AddScoped<AgentSessionJobProcessor>();
        services.AddScoped<AiSessionSweeper>();
        // Plan fase 2, C3.4: la retención del copiloto (hilos por tenant y tool_calls a los Ai:Copilot:ToolCallRetentionDays), una vez al día
        // desde el worker de la cola.
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AiOptions>>().Value.Copilot.ToRetention());
        services.AddScoped<CopilotRetentionSweeper>();
        services.AddSingleton<AiInboxWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<AiInboxWorker>());
        return services;
    }
}
