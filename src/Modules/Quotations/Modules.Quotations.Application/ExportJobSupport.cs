using System.Globalization;
using System.Text.Json;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Los filtros de un job, ida y vuelta a <c>export_jobs.filters</c>. Se guardan crudos —como los
/// mandó la pantalla y ya validados— y el procesador los vuelve a interpretar al generar.
/// </summary>
public static class ExportJobFilters
{
    public static string Serialize<TFilters>(TFilters filters) => JsonSerializer.Serialize(filters);

    /// <summary>D11: unos filtros que no se pueden leer no se arreglan reintentando.</summary>
    public static TFilters Read<TFilters>(ExportJob job)
        where TFilters : class
    {
        try
        {
            return JsonSerializer.Deserialize<TFilters>(job.Filters)
                ?? throw new ExportJobDefinitiveException("UnreadableFilters: the export filters are empty.");
        }
        catch (JsonException exception)
        {
            throw new ExportJobDefinitiveException($"UnreadableFilters: {exception.Message}", exception);
        }
    }
}

public static class ExportFileNames
{
    /// <summary>D8: <c>{prefijo}-yyyy-MM-dd-HHmm.xlsx</c> con la hora UTC en que se generó —la misma
    /// zona que el vencimiento que dice el correo—.</summary>
    public static string For(string prefix, DateTimeOffset generatedAt) =>
        $"{prefix}-{generatedAt.UtcDateTime.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.xlsx";
}
