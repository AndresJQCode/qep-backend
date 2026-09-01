using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.Channels;
using Modules.Notifications.Infrastructure.Messaging;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure;

public static class NotificationsInfrastructureExtensions
{
    public static IServiceCollection AddNotificationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<NotificationsDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "notifications")));

        var section = configuration.GetSection(NotificationsOptions.SectionName);
        services.AddOptions<NotificationsOptions>()
            .Bind(section)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NotificationsOptions>, NotificationsOptionsValidator>();

        // El proveedor de email se elige al momento de registrar, antes de que exista el
        // contenedor, así que este único valor hay que leerlo de forma ansiosa. Todo lo demás
        // se consume en runtime por IOptions<NotificationsOptions>.
        var provider = section[nameof(NotificationsOptions.EmailProvider)]
            ?? NotificationsOptions.LogProvider;
        AddEmailChannel(services, provider);

        services.AddHostedService(sp => new InvitationDeliveryWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<NotificationsOptions>>(),
            sp.GetRequiredService<ILogger<InvitationDeliveryWorker>>()));

        // Sin IOptions, a diferencia del de invitaciones: ese arma el enlace con
        // NotificationsOptions.InvitationUrl, y acá el enlace ya viene prefirmado en el evento.
        services.AddHostedService(sp => new CustomerExportDeliveryWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<CustomerExportDeliveryWorker>>()));

        return services;
    }

    private static void AddEmailChannel(IServiceCollection services, string provider)
    {
        if (string.Equals(provider, NotificationsOptions.InfobipProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailChannel>(sp =>
                new InfobipEmailChannel(
                    new HttpClient(),
                    sp.GetRequiredService<IOptions<NotificationsOptions>>()));
        }
        else
        {
            services.AddSingleton<IEmailChannel, LogEmailChannel>();
        }
    }
}
