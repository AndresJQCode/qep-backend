using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Excel;
using Modules.Quotations.Infrastructure.Expiration;
using Modules.Quotations.Infrastructure.Pdf;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Quotations.Infrastructure.Whatsapp;

namespace Modules.Quotations.Infrastructure;

public static class QuotationsInfrastructureExtensions
{
    public static IServiceCollection AddQuotationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<QuotationsDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "quotations")));

        services.AddScoped<IQuotationRepository, QuotationRepository>();
        services.AddScoped<IQuotationsUnitOfWork, QuotationsUnitOfWork>();
        // Escribe fuera de la transaccion del request, con un DbContext propio que arma con
        // los DbContextOptions que registro AddDbContext. Ver IQuotationSendFailureLog.
        services.AddScoped<IQuotationSendFailureLog, QuotationSendFailureLog>();
        services.AddScoped<IQuotationAuditPublisher, QuotationAuditPublisher>();
        services.AddScoped<IQuotationNumberGenerator, QuotationNumberGenerator>();
        services.AddScoped<ISaleRepository, SaleRepository>();
        services.AddScoped<ISaleNumberGenerator, SaleNumberGenerator>();
        // Sonda que Identity consulta antes de borrar un usuario huérfano (OrphanUserCleanupWorker).
        services.AddScoped<IUserReferenceProbe, QuotationUserReferenceProbe>();
        // El Excel del listado. ClosedXML se queda en esta capa: Application solo ve el puerto.
        services.AddScoped<IQuotationExportWorkbookBuilder, ClosedXmlQuotationExportBuilder>();

        var section = configuration.GetSection(QuotationsOptions.SectionName);
        services.AddOptions<QuotationsOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<QuotationsOptions>, QuotationsOptionsValidator>();
        services.AddScoped<IQuotationExpirationProcessor, QuotationExpirationProcessor>();
        services.AddHostedService<QuotationExpirationWorker>();

        AddPdfRenderer(services);
        AddWhatsAppSender(services, section.GetSection(nameof(QuotationsOptions.WhatsApp)));

        return services;
    }

    /// <summary>
    /// A diferencia de `AddWhatsAppSender` acá no hay registro condicional ni no-op: no
    /// existe un PDF de mentira que sirva. Sin la key, `qcode-pdf` responde 401 y el envío
    /// falla con `quotation.pdf.render_failed` — ruidoso y rastreable, que es lo que se
    /// quiere. En producción `QuotationsOptionsValidator` ni siquiera deja arrancar.
    /// </summary>
    private static void AddPdfRenderer(IServiceCollection services) =>
        services.AddSingleton<IQuotationPdfRenderer>(sp =>
            new QCodePdfRenderer(
                // Timeout propio: el default de HttpClient son 100 segundos, y `qcode-pdf`
                // corta la compilación a los 15 (`COMPILE_TIMEOUT_MS`). Sin esto, un
                // servicio caído deja colgado el request de la asesora un minuto y medio.
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                sp.GetRequiredService<IOptions<QuotationsOptions>>()));

    /// <summary>
    /// `Zenvia` sólo se registra cuando las tres claves están presentes — igual que
    /// `Notifications:EmailProvider=infobip` requiere sus tres, salvo que acá no hay un switch
    /// explícito: no configurarlas basta para caer al envío de desarrollo (`LogWhatsAppSender`),
    /// que no llama a nada externo. Así ningún `WebApplicationFactory` de las pruebas de
    /// integración —que no configuran Zenvia— tiene que empezar a hacerlo sólo porque "Enviar"
    /// ahora también manda un WhatsApp.
    /// </summary>
    private static void AddWhatsAppSender(IServiceCollection services, IConfigurationSection whatsApp)
    {
        var configured =
            !string.IsNullOrWhiteSpace(whatsApp[nameof(WhatsAppOptions.ApiToken)]) &&
            !string.IsNullOrWhiteSpace(whatsApp[nameof(WhatsAppOptions.FromNumber)]) &&
            !string.IsNullOrWhiteSpace(whatsApp[nameof(WhatsAppOptions.TemplateId)]);

        if (configured)
        {
            // `new HttpClient()` directo, sin `IHttpClientFactory` — mismo criterio que
            // `InfobipEmailChannel` en Notifications, el único otro cliente HTTP saliente del
            // backend.
            services.AddSingleton<IWhatsAppSender>(sp =>
                new ZenviaWhatsAppSender(
                    new HttpClient(),
                    sp.GetRequiredService<IOptions<QuotationsOptions>>(),
                    sp.GetRequiredService<ILogger<ZenviaWhatsAppSender>>()));
        }
        else
        {
            services.AddSingleton<IWhatsAppSender, LogWhatsAppSender>();
        }
    }
}
