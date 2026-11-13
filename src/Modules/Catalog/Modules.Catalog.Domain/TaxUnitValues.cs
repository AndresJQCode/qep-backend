using BuildingBlocks.Domain.Taxes;

namespace Modules.Catalog.Domain;

/// <summary>Resolución del valor de la unidad tributaria sobre los años cargados.</summary>
public static class TaxUnitValues
{
    /// <summary>
    /// El valor del año del documento; si ese año no está cargado, el del mayor año anterior. Null si no hay
    /// ninguno anterior: la tasa se congela sin unidad y no aplica.
    /// </summary>
    public static decimal? ValueFor(IEnumerable<TaxUnitValue> values, string country, int documentYear)
    {
        var code = TaxCountries.Find(country)?.Country;
        return values
            .Where(value => value.Country == code && value.Year <= documentYear)
            .OrderByDescending(value => value.Year)
            .Select(value => (decimal?)value.Value)
            .FirstOrDefault();
    }

    /// <summary>
    /// Desde el 24 de diciembre, true si el país tiene unidad tributaria y falta el valor del año siguiente.
    /// El aviso le da al operador una semana para cargarlo antes de que cambie el año.
    /// </summary>
    public static bool NextYearMissing(IEnumerable<TaxUnitValue> values, string country, DateOnly today)
    {
        var definition = TaxCountries.Find(country);
        if (definition?.TaxUnitName is null || today.Month != 12 || today.Day < 24)
        {
            return false;
        }

        return !values.Any(value => value.Country == definition.Country && value.Year == today.Year + 1);
    }
}
