using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Expiration;
using Modules.Quotations.Infrastructure.Persistence;

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
        services.AddScoped<IQuotationAuditPublisher, QuotationAuditPublisher>();
        services.AddScoped<IQuotationNumberGenerator, QuotationNumberGenerator>();
        services.AddScoped<ISaleRepository, SaleRepository>();
        services.AddScoped<ISaleNumberGenerator, SaleNumberGenerator>();
        // Sonda que Identity consulta antes de borrar un usuario huérfano (OrphanUserCleanupWorker).
        services.AddScoped<IUserReferenceProbe, QuotationUserReferenceProbe>();

        var section = configuration.GetSection(QuotationsOptions.SectionName);
        services.AddOptions<QuotationsOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<QuotationsOptions>, QuotationsOptionsValidator>();
        services.AddScoped<IQuotationExpirationProcessor, QuotationExpirationProcessor>();
        services.AddHostedService<QuotationExpirationWorker>();

        return services;
    }
}
