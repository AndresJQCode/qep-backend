using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    /// **Hoy no registra nada**: Reporting no tiene ninguna implementacion propia en esta capa.
    /// Se mantiene, y con <paramref name="configuration"/> en la firma aunque no se use, para que
    /// registrar el modulo se escriba como los otros seis en <c>AddQepPlatform</c>, y para que el
    /// dia que aparezca un servicio o una opcion de reporte no haya que cambiarle la forma a la
    /// llamada.
    /// </summary>
    public static IServiceCollection AddReportingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services;
    }
}
