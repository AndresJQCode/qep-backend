using Modules.Reporting.Domain;

namespace Modules.Reporting.Application;

/// <summary>
/// Los dos limites que comparten las cuatro exportaciones.
///
/// El workbook se arma completo en memoria antes de devolverse, asi que sin tope un tenant
/// grande tumba el proceso en vez de devolver un error. Cuando alguien alcance el tope, la
/// respuesta correcta no es subirlo: es acotar el reporte con los filtros que ya tiene.
/// </summary>
public static class ReportExportRules
{
    /// <summary>Tope duro de filas, el mismo que <c>ExportCustomersHandler</c>.</summary>
    public const int MaxExportRows = 50_000;

    /// <summary>
    /// Cuantas filas pedirle al origen: una mas que el tope, para poder distinguir "entro justo"
    /// de "se paso". Sin ese uno de mas, 50_000 exactas serian indistinguibles de 50_001.
    /// </summary>
    public const int ExportProbeLimit = MaxExportRows + 1;

    /// <summary>
    /// Falla si no hay nada para exportar o si hay demasiado.
    ///
    /// Vacio antes que pasado: son excluyentes, pero el orden fija cual gana si algun dia dejan
    /// de serlo. Un archivo con solo la cabecera es peor que decir que no habia nada — mismo
    /// criterio que <c>customers.export.empty</c>.
    /// </summary>
    public static void EnsureExportable(int rowCount)
    {
        if (rowCount == 0)
        {
            throw new ReportingDomainException(
                "reporting.export.empty",
                "There are no rows matching the report criteria.");
        }

        if (rowCount > MaxExportRows)
        {
            throw new ReportingDomainException(
                "reporting.export.too_many_rows",
                $"The report matches more than {MaxExportRows} rows. Narrow it with the filters.");
        }
    }
}
