using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Infrastructure.Excel;
using Modules.Hr.Infrastructure.Persistence;

namespace Modules.Hr.Infrastructure;

public static class HrInfrastructureExtensions
{
    public static IServiceCollection AddHrInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<HrDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    HrDbContext.Schema)));

        services.AddScoped<IHrUnitOfWork, HrUnitOfWork>();
        services.AddScoped<IHrAuditPublisher, HrAuditPublisher>();
        services.AddScoped<ILegalParameterSource, LegalParameterSource>();
        services.AddScoped<ILegalParameterRepository, LegalParameterRepository>();
        services.AddScoped<IHrSettingsRepository, HrSettingsRepository>();
        services.AddScoped<IEmployeeRepository, EmployeeRepository>();
        services.AddScoped<IEmployeeActivityLog, EmployeeActivityLog>();
        services.AddScoped<IMedicalExamRepository, MedicalExamRepository>();
        services.AddScoped<IMedicalExamOverviewReader, MedicalExamOverviewReader>();
        services.AddScoped<IHrMemberPhones, HrMemberPhones>();
        services.AddScoped<IExcelEmployeeImporter, ClosedXmlEmployeeImporter>();
        services.AddScoped<IEmployeeImportTemplateBuilder, ClosedXmlEmployeeImportTemplateBuilder>();
        services.AddScoped<IExcelLegalParameterImporter, ClosedXmlLegalParameterImporter>();
        services.AddScoped<ILegalParameterTemplateBuilder, ClosedXmlLegalParameterTemplateBuilder>();

        return services;
    }
}
