namespace Modules.Hr.Domain;

/// <summary>
/// Una nómina ya calculada, vista desde los valores legales: la fecha con la que se resolvieron (<c>legal_values_date</c>: el 1 o el
/// 16 según la quincena) y la empresa. Una por período calculado, de cualquier tenant: los valores legales son dato de la plataforma.
/// </summary>
public sealed record CalculatedPeriodStamp(DateOnly LegalValuesDate, Guid CompanyId);

/// <summary>Cuántos períodos y empresas cambiarían de resultado, y entre qué fechas de resolución.</summary>
public sealed record LegalParameterUsageSummary(int PeriodsAffected, int CompaniesAffected, DateOnly From, DateOnly To);

/// <summary>
/// Cuándo una fila de valor legal está «en uso» (spec 2026-11-22, «Uso»): una fila rige desde su fecha hasta la siguiente fila de la
/// misma clave, y está en uso si alguna nómina calculada se resolvió con una fecha dentro de ese rango. El mismo cálculo sirve para
/// crear, editar y borrar: el rango siempre es <c>[E, siguiente fecha)</c>.
/// </summary>
public static class LegalParameterUsageRule
{
    /// <summary>
    /// El resumen de lo que afecta tocar la fila de <paramref name="effectiveFrom"/>, o <c>null</c> si ninguna nómina calculada cae
    /// en su rango. <paramref name="otherEffectiveDates"/> son las demás fechas de la misma clave (sin <paramref name="effectiveFrom"/>)
    /// tal como quedarán después de todas las ediciones del lote.
    /// </summary>
    public static LegalParameterUsageSummary? Affected(
        DateOnly effectiveFrom,
        IEnumerable<DateOnly> otherEffectiveDates,
        IReadOnlyCollection<CalculatedPeriodStamp> stamps)
    {
        DateOnly? end = null;
        foreach (var date in otherEffectiveDates)
        {
            if (date > effectiveFrom && (end is null || date < end))
            {
                end = date;
            }
        }

        var inRange = stamps
            .Where(stamp => stamp.LegalValuesDate >= effectiveFrom && (end is null || stamp.LegalValuesDate < end))
            .ToList();
        if (inRange.Count == 0)
        {
            return null;
        }

        return new LegalParameterUsageSummary(
            inRange.Count,
            inRange.Select(stamp => stamp.CompanyId).Distinct().Count(),
            inRange.Min(stamp => stamp.LegalValuesDate),
            inRange.Max(stamp => stamp.LegalValuesDate));
    }
}
