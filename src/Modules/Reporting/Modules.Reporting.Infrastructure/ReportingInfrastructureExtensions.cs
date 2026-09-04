using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Reporting.Application;
using Modules.Reporting.Infrastructure.Excel;

namespace Modules.Reporting.Infrastructure;

/// <summary>
/// **Sin <c>AddDbContext</c> y sin inicializador de base**, a diferencia de los otros seis
/// modulos: Reporting no tiene DbContext, ni tablas, ni migraciones. Es lectura pura sobre datos
/// de otros modulos, y sus consultas viven en los adaptadores del composition root.
///
/// Por eso tampoco resuelve la cadena de conexion, y por eso <c>Program.cs</c> **no** llama a
/// ningun <c>InitializeReportingDatabaseAsync</c>: no hay ninguno que llamar.
/// </summary>
public static class ReportingInfrastructureExtensions
{
    /// <summary>
    /// <paramref name="configuration"/> no se usa hoy. Va igual en la firma para que registrar el
    /// modulo se escriba como los otros seis en <c>AddQepPlatform</c>, y para que el dia que
    /// aparezca una opcion de reporte no haya que cambiarle la forma a la llamada.
    /// </summary>
    public static IServiceCollection AddReportingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<IReportExcelBuilder, ClosedXmlReportExcelBuilder>();

        return services;
    }
}
