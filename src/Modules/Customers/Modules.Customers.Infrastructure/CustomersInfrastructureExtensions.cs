using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Excel;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Customers.Infrastructure.Phones;

namespace Modules.Customers.Infrastructure;

public static class CustomersInfrastructureExtensions
{
    public static IServiceCollection AddCustomersInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<CustomersDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "customers")));

        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IClientClassificationRepository, ClientClassificationRepository>();
        services.AddScoped<ICustomerNoteRepository, CustomerNoteRepository>();
        services.AddScoped<ICustomerTagRepository, CustomerTagRepository>();
        services.AddScoped<ICustomerAudienceRepository, CustomerAudienceRepository>();
        services.AddScoped<ICustomerTagLookup, CustomerTagLookup>();
        // Spec 2026-10-24 §3.2: coincidencias por número de documento normalizado.
        services.AddScoped<ICustomerDuplicateQueries, CustomerDuplicateQueries>();
        services.AddScoped<ICustomersUnitOfWork, CustomersUnitOfWork>();
        // Spec 2026-10-24 §3.2–§3.4: lo que la fusión lee y escribe en customers sin pasar por el agregado.
        services.AddScoped<ICustomerMergeStore, CustomerMergeStore>();
        services.AddScoped<ICustomersAuditPublisher, CustomersAuditPublisher>();
        services.AddScoped<ICucGenerator, CucGenerator>();

        // Fase 5/6: la libreria concreta (ClosedXML) es un detalle de infraestructura, mismo
        // criterio que el resto de este metodo. Application solo conoce los puertos.
        services.AddScoped<IExcelCustomerImporter, ClosedXmlCustomerImporter>();
        services.AddScoped<ICustomerImportTemplateBuilder, ClosedXmlCustomerImportTemplateBuilder>();
        services.AddScoped<ICustomerExportBuilder, ClosedXmlCustomerExportBuilder>();

        // El adaptador que sube a R2 no vive aca sino en el composition root: necesita
        // Modules.Storage, y este modulo no lo referencia.
        services.AddScoped<ICustomerExportEventPublisher, CustomerExportEventPublisher>();

        // Spec 2026-10-09 §6.5: phone_e164 para que Messaging empareje por teléfono.
        services.AddSingleton<IPhoneNumberNormalizer, LibPhoneNumberNormalizer>();
        services.AddScoped<ICustomerPhoneDirectory, CustomerPhoneDirectory>();
        // Spec 2026-10-10 §6.2: el cliente de quien escribe por WhatsApp (asegurar, BSUID, nombres, búsqueda).
        services.AddScoped<ICustomerWhatsAppDirectory, CustomerWhatsAppDirectory>();
        // Spec 2026-10-14 decisión 10: la ficha incompleta que nace de un DM de Instagram.
        services.AddScoped<ICustomerInstagramDirectory, CustomerInstagramDirectory>();
        // Spec de Shopify 2026-10-15 §6.2.1: altas de compradores desde una integración, sin persona detrás.
        services.AddScoped<ICustomerProvisioning, CustomerProvisioning>();
        // Spec 2026-10-26 D-5: el registro de consentimiento de datos, sólo de inserción.
        services.AddScoped<ICustomerDataConsentRepository, CustomerDataConsentRepository>();
        services.AddHostedService<CustomerPhoneBackfillWorker>();

        return services;
    }
}
