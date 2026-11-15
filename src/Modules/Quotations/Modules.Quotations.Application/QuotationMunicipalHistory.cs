using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El historial de la retención municipal de una cotización (spec ReteICA por empresa §7.8): lo que va al resumen del
/// <c>Edited</c> (municipio de la operación, aplicación a mano) y una entrada <c>WithholdingRateEdited</c>, con su
/// auditoría, por cada tarifa editada que se perdió o que cambió. Sólo lo escriben el guardado y el cambio de cliente: las
/// vistas previas y la creación no (plan P13).
/// </summary>
internal static class QuotationMunicipalHistory
{
    public const string RateEditedAuditAction = "quotation.quotation.withholding_rate_edited";

    /// <summary>
    /// Las frases que se agregan al resumen del <c>Edited</c>: el cambio de municipio (sólo de uno a otro: la primera vez
    /// que se fija no tiene texto en el spec) y cada tasa aplicada a mano con facturación propia, con su municipio.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SummaryPartsAsync(
        IQuotationGeographyLookup geography, QuotationEditsOutcome outcome, CancellationToken cancellationToken)
    {
        var municipal = outcome.Municipal;
        var cityChanged = municipal.Triggered
            && municipal.OperationCityBefore is not null
            && municipal.OperationCityAfter is not null
            && municipal.OperationCityBefore != municipal.OperationCityAfter;
        Guid[] cityIds =
        [
            .. new[] { cityChanged ? municipal.OperationCityBefore : null, cityChanged ? municipal.OperationCityAfter : null }
                .Concat(outcome.ManualMunicipalRates.Select(rate => rate.CityId))
                .OfType<Guid>()
                .Distinct(),
        ];
        if (cityIds.Length == 0)
        {
            return [];
        }

        var names = await geography.FindCityNamesAsync(cityIds, cancellationToken);
        var parts = new List<string>();
        if (cityChanged
            && names.TryGetValue(municipal.OperationCityBefore!.Value, out var from)
            && names.TryGetValue(municipal.OperationCityAfter!.Value, out var to))
        {
            parts.Add(QuotationChangeSummary.OperationCityChanged(from, to));
        }

        foreach (var rate in outcome.ManualMunicipalRates)
        {
            // Una ciudad que Geography ya no tiene se nombra con la tasa: el historial nunca muestra un id.
            var city = rate.CityId is { } cityId && names.TryGetValue(cityId, out var name) ? name : rate.Name;
            parts.Add(QuotationChangeSummary.ManualMunicipalWithholdingApplied(rate.Type, city) + ".");
        }

        return parts;
    }

    /// <summary>
    /// Una entrada <c>WithholdingRateEdited</c> por cada tarifa editada que se descartó y después por cada cambio de
    /// porcentaje, en ese orden (spec §7.7). Cada una va un microsegundo después de la anterior: el historial ordena por
    /// fecha y dos entradas en el mismo instante saldrían en cualquier orden.
    /// </summary>
    public static void RecordRateEdits(
        IQuotationRepository repository,
        IQuotationAuditPublisher auditPublisher,
        Guid subjectId,
        Guid tenantId,
        Quotation quotation,
        IEnumerable<WithholdingRate> discarded,
        IEnumerable<WithholdingPercentageChange> changes,
        HistoryFormat format,
        MemberId updatedBy,
        DateTimeOffset now)
    {
        var texts = discarded
            .Select(rate => QuotationChangeSummary.WithholdingOverrideDiscarded(rate.Type, rate.Percentage, format))
            .Concat(changes.Select(change => change.Restored
                ? QuotationChangeSummary.WithholdingRateRestored(change.Name, change.To, format)
                : QuotationChangeSummary.WithholdingRateEdited(change.Name, change.From, change.To, format)));
        var position = 0;
        foreach (var text in texts)
        {
            var at = now.AddTicks(TimeSpan.TicksPerMicrosecond * ++position);
            repository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                QuotationHistoryEventType.WithholdingRateEdited,
                updatedBy,
                text,
                at));
            auditPublisher.Publish(tenantId, subjectId, RateEditedAuditAction, quotation.Id.ToString(), "success", at);
        }
    }
}
