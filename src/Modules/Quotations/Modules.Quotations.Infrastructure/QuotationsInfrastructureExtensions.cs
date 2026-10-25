using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Excel;
using Modules.Quotations.Infrastructure.Expiration;
using Modules.Quotations.Infrastructure.Exports;
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
        // La cola de exportaciones (spec 2026-09-12): la tabla, la toma con SKIP LOCKED y los dos
        // eventos para Notifications. El runner que los usa se registra en Bootstrapper.
        services.AddScoped<IExportJobQueue, ExportJobQueue>();
        services.AddScoped<IExportEventPublisher, ExportJobEventPublisher>();
        // Spec 2026-09-16, D9: el aviso a Storage de que un pedido adjuntó comprobantes públicos.
        services.AddScoped<IOrderPaymentProofEventPublisher, OrderPaymentProofEventPublisher>();
        // Spec 2026-10-13 §4.5: enviada, convertida y pedido anulado, para Pipeline. En la misma unidad de trabajo que el cambio.
        services.AddScoped<IQuotationLifecycleEventPublisher, QuotationLifecycleEventPublisher>();
        services.AddHostedService<ExportJobWorker>();
        // Sin estado: una instancia por proceso alcanza. Cada export crea su propio temporal.
        services.AddSingleton<IExportWorkbookWriter, OpenXmlExportWorkbookWriter>();
        services.AddScoped<IQuotationNumberGenerator, QuotationNumberGenerator>();
        // Spec 2026-09-17: el formato del número es un dato por tenant. Scoped porque lee por el
        // DbContext del request, igual que los generadores de consecutivo.
        services.AddScoped<IDocumentNumberingFormatLookup, DocumentNumberingFormatLookup>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOrderNumberGenerator, OrderNumberGenerator>();
        // El layout de columnas del Excel de pedidos (spec 2026-09-24): lo leen el PUT/GET del
        // tenant y el processor, cada uno en su scope.
        services.AddScoped<IOrdersExportLayoutRepository, OrdersExportLayoutRepository>();
        // The tenant's minimum purchase (spec 2026-10-08, D7): read by the recalculation and the
        // response composer, written by PUT /quotations/settings, all on the request's DbContext.
        services.AddScoped<IQuotationSettingsStore, QuotationSettingsStore>();
        // Las transportadoras del tenant (spec 2026-10-17 §4).
        services.AddScoped<ICarrierRepository, CarrierRepository>();
        // Sonda que Identity consulta antes de borrar un usuario huérfano (OrphanUserCleanupWorker).
        services.AddScoped<IUserReferenceProbe, QuotationUserReferenceProbe>();
        // Sonda que Storage consulta antes de purgar un comprobante en staging (spec 2026-09-16, D11).
        services.AddScoped<IFileReferenceProbe, OrderPaymentProofFileReferenceProbe>();
        // Sonda que Storage consulta antes de borrar un objeto huérfano de payment-proofs/ (D12).
        services.AddScoped<IPublicObjectReferenceProbe, OrderPaymentProofPublicObjectReferenceProbe>();

        var section = configuration.GetSection(QuotationsOptions.SectionName);
        services.AddOptions<QuotationsOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<QuotationsOptions>, QuotationsOptionsValidator>();
        services.AddScoped<IQuotationExpirationProcessor, QuotationExpirationProcessor>();
        services.AddHostedService<QuotationExpirationWorker>();

        AddPdfRenderer(services);
        AddWhatsAppSender(services);

        return services;
    }

    /// <summary>
    /// Igual que `AddWhatsAppSender`, sin registro condicional ni no-op: no
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
    /// Zenvia sin cuenta propia: el token, el número y las plantillas llegan en cada envío
    /// como el canal del tenant (plan 2026-10-21). No hay sender de log ni cuenta global: sin canal el
    /// envío responde `quotation.whatsapp.no_channel`, y las pruebas registran dobles sólo en su host
    /// (spec 2026-10-21 D18). El puerto de Meta (<see cref="IMetaWhatsAppSender"/>) lo registra Bootstrapper.
    /// </summary>
    private static void AddWhatsAppSender(IServiceCollection services)
    {
        // `new HttpClient()` directo, sin `IHttpClientFactory` — mismo criterio que
        // `InfobipEmailChannel` en Notifications, el único otro cliente HTTP saliente del backend.
        services.AddSingleton<IZenviaWhatsAppSender>(sp =>
            new ZenviaWhatsAppSender(
                new HttpClient(),
                sp.GetRequiredService<IOptions<QuotationsOptions>>(),
                sp.GetRequiredService<ILogger<ZenviaWhatsAppSender>>()));

        // El Strategy por proveedor (spec 2026-10-21 D17). Scoped: el puerto de Meta cruza a Mensajería y usa el
        // DbContext del request.
        services.AddScoped<IWhatsAppSender, WhatsAppSenderRouter>();
    }
}
