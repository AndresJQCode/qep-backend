using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Application;
using Modules.Pipeline.Infrastructure.Persistence;

namespace Modules.Pipeline.Infrastructure;

public static class PipelineInfrastructureExtensions
{
    public static IServiceCollection AddPipelineInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<PipelineDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PipelineDbContext.Schema)));

        // Spec 2026-10-13 D4: el embudo y sus semillas en el primer acceso.
        services.AddScoped<IPipelineProvisioner, PipelineProvisioner>();
        services.AddScoped<IDealRepository, DealRepository>();
        services.AddScoped<IDealQuotationRepository, DealQuotationRepository>();
        services.AddScoped<IPipelineReasonRepository, PipelineReasonRepository>();
        services.AddScoped<ITriageDecisionRepository, TriageDecisionRepository>();
        // P4 del plan: la unidad de trabajo guarda el historial pendiente de cada negocio.
        services.AddScoped<IPipelineUnitOfWork, PipelineUnitOfWork>();
        // §6.3: auditoría atómica propia (nunca el IAuditRecorder de Tenancy).
        services.AddScoped<IPipelineAuditRecorder, PipelineAuditRecorder>();
        return services;
    }
}
