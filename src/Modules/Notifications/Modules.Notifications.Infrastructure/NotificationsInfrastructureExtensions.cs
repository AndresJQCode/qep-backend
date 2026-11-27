using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.Channels;
using Modules.Notifications.Infrastructure.HrBirthdays;
using Modules.Notifications.Infrastructure.InApp;
using Modules.Notifications.Infrastructure.Messaging;
using Modules.Notifications.Infrastructure.Persistence;
using Modules.Notifications.Infrastructure.TaskNotices;

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

        // Los consumidores de correo son definiciones singleton, no hosted services: los drena en secuencia un solo
        // bucle, NotificationsDeliveryWorker (spec 2026-11-13, D-7). Ese hosted va por tipo y no por factoría, así el
        // descriptor lleva ImplementationType y el harness de pruebas puede sacarlo (spec 2026-09-13). El de
        // invitaciones recibe IOptions<NotificationsOptions> por constructor, porque arma el enlace con InvitationUrl;
        // los de exportación no, porque su enlace ya viene prefirmado en el evento.
        services.AddSingleton<OutboxDeliveryWorker, InvitationDeliveryWorker>();
        // Correos de cuenta con contraseña: reciben IOptions por constructor porque arman el enlace con
        // AppBaseUrl.
        services.AddSingleton<OutboxDeliveryWorker, EmailVerificationDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, PasswordResetDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, ExistingAccountDeliveryWorker>();
        // Spec 2026-11-19 (onboarding por link): el código que confirma un correo sin usuario; sin enlace, sin AppBaseUrl.
        services.AddSingleton<OutboxDeliveryWorker, EmailChallengeDeliveryWorker>();
        // Correos de la cuenta de comprador: el destinatario viene en el payload del evento.
        services.AddSingleton<OutboxDeliveryWorker, BuyerVerificationDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, BuyerExistingAccountDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, BuyerPasswordResetDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, BuyerAccountDeletedDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, CustomerExportDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, ProductExportDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, SupplierExportDeliveryWorker>();
        // Spec WMS 2026-10-31, S15: el export de saldos de bodega.
        services.AddSingleton<OutboxDeliveryWorker, WmsExportDeliveryWorker>();
        // Spec 2026-11-17 (mayorista): la invitación a un distribuidor, con el link armado sobre AppBaseUrl (ruling W1).
        services.AddSingleton<OutboxDeliveryWorker, WholesaleInvitationDeliveryWorker>();
        // Spec 2026-11-19 §6: el correo del distribuidor cambió al aceptar. Un consumidor por dirección, cada uno con su inbox.
        services.AddSingleton<OutboxDeliveryWorker, WholesaleEmailChangedToPreviousDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, WholesaleEmailChangedToNewDeliveryWorker>();
        // La exportación asíncrona de Quotations (spec 2026-09-12, D12): una definición por evento.
        services.AddSingleton<OutboxDeliveryWorker, QuotationsExportReadyDeliveryWorker>();
        services.AddSingleton<OutboxDeliveryWorker, QuotationsExportFailedDeliveryWorker>();
        // Spec 2026-11-22 §12: the bell's consumers, drained by the same loop. One definition per source.
        services.AddSingleton<InAppOutboxConsumer, ExportsInAppConsumer>();
        services.AddSingleton<InAppOutboxConsumer, ConversationsInAppConsumer>();
        services.AddSingleton<InAppOutboxConsumer, WholesaleInAppConsumer>();
        services.AddSingleton<InAppOutboxConsumer, BillingInAppConsumer>();
        services.AddSingleton<InAppOutboxConsumer, SupportInAppConsumer>();
        services.AddSingleton<InAppOutboxConsumer, HrInAppConsumer>();
        services.AddScoped<IInAppNotificationRepository, InAppNotificationRepository>();
        services.AddHostedService<NotificationsDeliveryWorker>();
        services.AddHostedService<InAppNotificationRetentionWorker>();
        // Task-notices spec 2026-11-22 §5.4: the escalation of unread assignments, a pass a minute; ports implemented in Bootstrapper.
        services.AddScoped<TaskNoticeEscalationProcessor>();
        services.AddHostedService<TaskNoticeEscalationWorker>();
        // HR birthday notices spec 2026-11-26 §5.3: the WhatsApp and email deliveries of the daily digest, a pass a minute; the WhatsApp port is
        // implemented in Bootstrapper.
        services.AddScoped<HrBirthdayDeliveryProcessor>();
        services.AddHostedService<HrBirthdayDeliveryWorker>();

        return services;
    }

    private static void AddEmailChannel(IServiceCollection services, string provider)
    {
        if (string.Equals(provider, NotificationsOptions.InfobipProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailChannel>(sp =>
                new InfobipEmailChannel(
                    InfobipEmailChannel.CreateHttpClient(),
                    sp.GetRequiredService<IOptions<NotificationsOptions>>()));
        }
        else
        {
            services.AddSingleton<IEmailChannel, LogEmailChannel>();
        }
    }
}
