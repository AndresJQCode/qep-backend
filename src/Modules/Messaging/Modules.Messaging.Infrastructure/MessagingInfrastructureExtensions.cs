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
        // Spec 2026-10-09 §8.2: la firma del webhook y la cola deduplicada de entregas.
        services.AddSingleton<IWebhookSignatureVerifier, HmacWebhookSignatureVerifier>();
        services.AddScoped<IWebhookDeliveries, WebhookDeliveries>();

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
