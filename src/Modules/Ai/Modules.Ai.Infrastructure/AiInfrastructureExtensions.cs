using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Ai.Infrastructure.Model;
using Modules.Ai.Infrastructure.Model.Anthropic;
using Modules.Ai.Infrastructure.Options;
using Modules.Ai.Infrastructure.Persistence;

namespace Modules.Ai.Infrastructure;

public static class AiInfrastructureExtensions
{
    public static IServiceCollection AddAiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Plan 2026-11-12, C-3: la lista curada por proveedor. Rota, el pod no arranca: un agente no podría publicarse.
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IAiModelCatalog, ConfiguredAiModelCatalog>();

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
        services.AddKeyedScoped<IAiModelClient, PendingProviderModelClient>(AiProviders.OpenAi.Value);
        services.AddSingleton<IValidateOptions<AiModelClientRegistrationOptions>, AiModelClientRegistrationValidator>();
        services.AddOptions<AiModelClientRegistrationOptions>().ValidateOnStart();

        // Plan 2026-11-12, 5.5: el bucle común, sus herramientas de lectura (los puertos van en Bootstrapper) y lo que deja escrito.
        services.AddScoped<AgentPromptBuilder>();
        services.AddScoped<AgentTurnRunner>();
        services.AddScoped<IAgentTool, SearchCatalogTool>();
        services.AddScoped<IAgentTool, GetOrderStatusTool>();
        services.AddScoped<IAgentTool, SimulatedQuotationDraftTool>();
        services.AddScoped<IAiRunLedger, AiRunLedger>();

        // Spec 2026-10-29 §7: el playground, con la cola ai.model_jobs y su worker.
        services.AddScoped<IAgentTestRepository, AgentTestRepository>();
        services.AddScoped<IAiModelJobs, AiModelJobs>();
        services.AddScoped<AgentTestJobProcessor>();
        services.AddOptions<AiWorkerOptions>().Bind(configuration.GetSection(AiWorkerOptions.SectionName));
        services.AddSingleton<AiModelWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<AiModelWorker>());
        return services;
    }
}
