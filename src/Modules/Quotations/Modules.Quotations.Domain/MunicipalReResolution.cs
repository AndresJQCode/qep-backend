using BuildingBlocks.Domain.Taxes;

namespace Modules.Quotations.Domain;

/// <summary>
/// Lo que pasó con las retenciones municipales en una edición (spec ReteICA por empresa §7.6), para que la aplicación
/// escriba el historial: si hubo re-resolución, el municipio de antes y el de después, las filas con porcentaje editado
/// que se perdieron y, en el pedido, las aplicadas a mano que se quitaron porque el cliente no es agente ahí.
/// </summary>
public sealed record MunicipalReResolution(
    bool Triggered,
    Guid? OperationCityBefore,
    Guid? OperationCityAfter,
    IReadOnlyList<WithholdingRate> DiscardedOverrides,
    IReadOnlyList<WithholdingRate> RemovedManual)
{
    /// <summary>No hubo cambio de empresa, de municipio ni de cliente: nada se re-resolvió.</summary>
    public static MunicipalReResolution None { get; } = new(false, null, null, [], []);
}

/// <summary>Un cambio del porcentaje efectivo de una retención municipal (spec §7.7), para el historial.</summary>
/// <param name="Name">El nombre de la tasa congelada.</param>
/// <param name="From">El porcentaje efectivo de antes.</param>
/// <param name="To">El porcentaje efectivo de ahora.</param>
/// <param name="Restored">Si volvió al porcentaje del catálogo.</param>
public sealed record WithholdingPercentageChange(string Name, decimal From, decimal To, bool Restored);

/// <summary>
/// La re-resolución de una fuente de retenciones (spec §7.6), compartida por la cotización y el pedido. Las nacionales no
/// se tocan; cada fila municipal se reemplaza en su posición o se quita; con la regla del cliente, un tipo que no tenía
/// fila puede ganarla al final.
/// </summary>
internal static class MunicipalReResolver
{
    /// <summary>Si el país tiene algún tipo que se causa por municipio.</summary>
    public static bool HasMunicipalTypes(string? country) => TaxCountries.MunicipalTypes(country).Count > 0;

    /// <summary>Si la foto es de un tipo municipal en su propio país.</summary>
    public static bool IsMunicipal(WithholdingRate rate) => TaxCountries.IsMunicipal(rate.Country, rate.Type);

    /// <summary>
    /// La fuente del cliente al nacer o al cambiar el cliente: sus tasas nacionales (el cliente ya no puede tener
    /// municipales; una que llegue no entra) más, por cada tipo municipal ofrecido, la regla del cliente.
    /// </summary>
    public static IReadOnlyList<WithholdingRate> ByCustomerRule(
        IEnumerable<WithholdingRate> customerRates, MunicipalContext municipal, Guid? operationCityId) =>
    [
        .. customerRates.Where(rate => !IsMunicipal(rate)),
        .. municipal.Offers
            .Select(offer => MunicipalWithholdings.ByCustomerRule(offer, operationCityId))
            .OfType<WithholdingRate>(),
    ];

    /// <summary>
    /// Re-resuelve una fuente con el contexto nuevo.
    /// </summary>
    /// <param name="current">Las filas de la fuente, en su orden.</param>
    /// <param name="customerRule">true para la fuente del cliente y el pedido; false para el selector de facturación propia
    /// (plan P5), que sólo mueve lo que ya estaba elegido.</param>
    public static (IReadOnlyList<WithholdingRate> Rates, IReadOnlyList<WithholdingRate> Discarded, IReadOnlyList<WithholdingRate> RemovedManual)
        ReResolve(
            IReadOnlyList<WithholdingRate> current,
            MunicipalContext municipal,
            Guid? operationCityId,
            bool customerRule)
    {
        var rates = new List<WithholdingRate>();
        var discarded = new List<WithholdingRate>();
        var removedManual = new List<WithholdingRate>();
        var resolvedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rate in current)
        {
            if (!IsMunicipal(rate))
            {
                rates.Add(rate);
                continue;
            }

            // Sin oferta del tipo (la empresa nueva es de un país sin tipos municipales, o de otro país) la fila se quita.
            var offer = municipal.Offers.FirstOrDefault(candidate =>
                string.Equals(candidate.Type, rate.Type, StringComparison.Ordinal));
            var replacement = offer is null
                ? null
                : customerRule
                    ? MunicipalWithholdings.ByCustomerRule(offer, operationCityId)
                    : MunicipalWithholdings.BySelectorRule(offer, operationCityId, hadSelection: true);
            resolvedTypes.Add(rate.Type);
            if (rate.Overridden)
            {
                discarded.Add(rate);
            }

            if (replacement is not null)
            {
                rates.Add(replacement);
            }
            else if (offer is { CompanyRate: not null, CustomerIsAgent: false } && operationCityId is not null)
            {
                // La empresa sí tiene tasa ahí, pero el cliente no es agente: la que estaba era una aplicación a mano.
                removedManual.Add(rate);
            }
        }

        if (customerRule)
        {
            rates.AddRange(municipal.Offers
                .Where(offer => !resolvedTypes.Contains(offer.Type))
                .Select(offer => MunicipalWithholdings.ByCustomerRule(offer, operationCityId))
                .OfType<WithholdingRate>());
        }

        return (rates, discarded, removedManual);
    }

    /// <summary>Las reglas del porcentaje editado (spec §7.7), con el prefijo de código de cada documento. Devuelve el
    /// porcentaje efectivo que va a quedar.</summary>
    public static decimal EnsureEditablePercentage(DocumentWithholding? row, decimal? percentage, string codePrefix)
    {
        if (row is null || !TaxCountries.IsMunicipal(row.Country, row.Type))
        {
            throw new QuotationsDomainException(
                $"{codePrefix}.withholding_percentage_not_editable",
                "Only the percentage of a municipal withholding applied to the document can be edited.");
        }

        if (percentage is { } value
            && (value <= 0m || value > 100m || decimal.Round(value, TaxCountries.MaxFractionDigits) != value))
        {
            throw new QuotationsDomainException(
                $"{codePrefix}.withholding_percentage_invalid",
                "The withholding percentage must be greater than 0, at most 100 and have at most 3 decimals.");
        }

        return percentage ?? row.CatalogPercentage;
    }
}
