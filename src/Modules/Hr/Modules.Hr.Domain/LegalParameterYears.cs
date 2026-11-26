namespace Modules.Hr.Domain;

/// <summary>La cobertura anual de los valores legales: qué año le falta a una clave y desde cuándo se avisa del siguiente.</summary>
public static class LegalParameterYears
{
    /// <summary>Falta el año si ninguna fila rige exactamente desde el 1 de enero de ese año.</summary>
    public static bool MissingFor(IEnumerable<DateOnly> effectiveDates, int year)
    {
        var januaryFirst = new DateOnly(year, 1, 1);
        return !effectiveDates.Contains(januaryFirst);
    }

    /// <summary>El aviso del año siguiente sólo se abre desde el 1 de diciembre, en el calendario del operador.</summary>
    public static bool NextYearWatchOpen(DateOnly today) => today.Month == 12;
}
