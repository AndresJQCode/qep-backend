using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Arma la pantalla entera de valores legales (spec 2026-11-22, «Forma de las respuestas»). La comparten el <c>GET</c>, que la
/// consulta, y cada <c>PUT</c>/<c>DELETE</c>, que la devuelven ya recompuesta. Lee por el repositorio de escritura y no por la caché:
/// una pantalla de operador nunca puede mostrar una tabla vieja. Los sellos de las nóminas calculadas se leen <b>una sola vez</b> y se
/// cruzan en memoria con todas las filas.
/// </summary>
public sealed class LegalParametersComposer(
    ILegalParameterRepository repository,
    ILegalParameterUsage usage,
    ITenantClock tenantClock)
{
    public async Task<LegalParametersDto> ComposeAsync(TenantId operatorTenantId, CancellationToken cancellationToken)
    {
        var calendar = await tenantClock.GetAsync(operatorTenantId.Value, cancellationToken);
        var today = calendar.Today;
        var currentYear = today.Year;
        var nextYear = currentYear + 1;
        var watchOpen = LegalParameterYears.NextYearWatchOpen(today);

        var values = await repository.ListValuesAsync(cancellationToken);
        var bracketRows = await repository.ListBracketsAsync(cancellationToken);
        var stamps = await usage.ListCalculatedPeriodsOfAllTenantsAsync(cancellationToken);

        bool InUse(DateOnly effectiveFrom, IReadOnlyCollection<DateOnly> dates) =>
            LegalParameterUsageRule.Affected(effectiveFrom, dates.Where(date => date != effectiveFrom), stamps) is not null;

        var parameters = new List<LegalParameterKeyDto>();
        foreach (var key in Enum.GetValues<LegalParameterKey>())
        {
            var label = LegalParameterLabels.Of(key);
            var rows = values
                .Where(value => value.Key == key)
                .OrderByDescending(value => value.EffectiveFrom)
                .ToList();
            var dates = rows.Select(row => row.EffectiveFrom).ToList();
            var current = rows.FirstOrDefault(row => row.EffectiveFrom <= today);
            parameters.Add(new LegalParameterKeyDto(
                key.ToString(),
                label.Name,
                label.Group.ToString(),
                label.Unit.ToString(),
                label.Yearly,
                label.Yearly && watchOpen && LegalParameterYears.MissingFor(dates, nextYear),
                label.Yearly && LegalParameterYears.MissingFor(dates, currentYear),
                current is null ? null : new LegalParameterCurrentDto(current.EffectiveFrom, current.Value),
                rows.Select(row => new LegalParameterRowDto(row.EffectiveFrom, row.Value, InUse(row.EffectiveFrom, dates), row.UpdatedAt))
                    .ToList()));
        }

        var brackets = new List<LegalBracketKeyDto>();
        foreach (var key in Enum.GetValues<LegalBracketKey>())
        {
            var sets = bracketRows
                .Where(row => row.Key == key)
                .GroupBy(row => row.EffectiveFrom)
                .OrderByDescending(group => group.Key)
                .ToList();
            var dates = sets.Select(set => set.Key).ToList();
            var current = sets.FirstOrDefault(set => set.Key <= today);
            brackets.Add(new LegalBracketKeyDto(
                key.ToString(),
                LegalParameterLabels.NameOf(key),
                watchOpen && LegalParameterYears.MissingFor(dates, nextYear),
                LegalParameterYears.MissingFor(dates, currentYear),
                current is null ? null : new LegalBracketCurrentDto(current.Key),
                sets.Select(set => new LegalBracketSetDto(
                        set.Key,
                        InUse(set.Key, dates),
                        set.Max(row => row.UpdatedAt),
                        set.OrderBy(row => row.LowerBound)
                            .Select(row => new LegalBracketDto(row.LowerBound, row.UpperBound, row.Rate))
                            .ToList()))
                    .ToList()));
        }

        return new LegalParametersDto(
            today,
            currentYear,
            parameters.Count(item => item.CurrentYearMissing) + brackets.Count(item => item.CurrentYearMissing),
            nextYear,
            parameters.Count(item => item.NextYearMissing) + brackets.Count(item => item.NextYearMissing),
            parameters,
            brackets);
    }
}
