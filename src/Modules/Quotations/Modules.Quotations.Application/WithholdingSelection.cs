using BuildingBlocks.Domain.Taxes;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Qué ids municipales acepta un selector (spec ReteICA por empresa §7.5, §7.6; plan P6).
/// </summary>
/// <param name="AllowedMunicipalRateIds">Las tasas de la empresa para el municipio de la operación resultante (las
/// <c>companyRate</c> del contexto): un id municipal fuera de acá no es elegible.</param>
/// <param name="IgnoreUnmatched">true cuando el mismo cuerpo cambió la empresa o el municipio (A11, owner 2026-11-13): un id
/// municipal fuera de la lista se ignora en silencio en vez de dar 422, porque se eligió contra lo anterior.</param>
public sealed record MunicipalSelectorScope(IReadOnlySet<Guid> AllowedMunicipalRateIds, bool IgnoreUnmatched)
{
    /// <summary>Sin contexto municipal: ningún id municipal es elegible y nada se ignora.</summary>
    public static MunicipalSelectorScope None { get; } = new(new HashSet<Guid>(), false);

    /// <summary>
    /// Las filas municipales que la re-resolución dejó en la fuente (plan P6). Con <see cref="IgnoreUnmatched"/>, cada tipo
    /// sin un id aceptado conserva la suya, al final de la selección; un id aceptado la reemplaza.
    /// </summary>
    public IReadOnlyList<WithholdingRate> ReResolved { get; init; } = [];

    /// <summary>El alcance de un contexto: aceptadas las tasas que la empresa ofrece en el municipio.</summary>
    public static MunicipalSelectorScope For(MunicipalContext context, bool ignoreUnmatched) =>
        new(
            context.Offers
                .Select(offer => offer.CompanyRate?.TaxRateId)
                .OfType<Guid>()
                .ToHashSet(),
            ignoreUnmatched);
}

/// <summary>
/// Resuelve contra el catálogo los ids que llegan en los selectores de retención (spec impuestos por tipo §7.5, D23,
/// plan P11): cada uno existe en el tenant, está activo, retiene y es del país de la empresa facturadora, y hay uno
/// por tipo. La comparten la cotización y el pedido, cada uno con su prefijo de código. El agregado vuelve a exigir
/// país y unicidad por tipo al guardar (<c>WithholdingSelectorRules</c>); acá están las reglas que sólo se ven con
/// el catálogo en la mano.
/// </summary>
public static class WithholdingSelection
{
    /// <param name="companyCountry">El país de la empresa facturadora; null sin empresa, y entonces ningún selector
    /// sirve.</param>
    /// <param name="scope">Qué ids municipales se aceptan y si los demás se ignoran (spec ReteICA por empresa §7.6).</param>
    /// <param name="codePrefix"><c>quotation</c> u <c>order</c>.</param>
    /// <param name="clearedTypes">Los tipos marcados «Sin retención» (plan P12): no reciben la fila re-resuelta.</param>
    /// <returns>Las fotos en el orden de <paramref name="ids"/>, sin las ignoradas y con las re-resueltas al final;
    /// vacía si no llegó ninguno.</returns>
    public static async Task<IReadOnlyList<WithholdingRate>> ResolveAsync(
        IQuotationWithholdingLookup lookup,
        Guid tenantId,
        IReadOnlyCollection<Guid> ids,
        string? companyCountry,
        DateOnly documentDate,
        MunicipalSelectorScope scope,
        string codePrefix,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? clearedTypes = null)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(scope);
        var reResolved = scope.IgnoreUnmatched ? scope.ReResolved : [];
        if (ids.Count == 0 && reResolved.Count == 0)
        {
            return [];
        }

        // Sin empresa no hay país contra el que validar: ni se consulta el catálogo.
        var country = companyCountry?.Trim();
        if (string.IsNullOrEmpty(country))
        {
            throw Invalid(codePrefix);
        }

        var candidates = ids.Count == 0
            ? new Dictionary<Guid, WithholdingRateCandidate>()
            : await lookup.ForRatesAsync(tenantId, ids, documentDate, cancellationToken);
        var rates = new List<WithholdingRate>(ids.Count + reResolved.Count);
        foreach (var id in ids)
        {
            if (!candidates.TryGetValue(id, out var candidate) || !candidate.IsActive || !candidate.Withholds)
            {
                throw Invalid(codePrefix);
            }

            var municipal = TaxCountries.IsMunicipal(candidate.Rate.Country, candidate.Rate.Type);
            if (municipal && !scope.AllowedMunicipalRateIds.Contains(id))
            {
                // A11: tras un cambio de empresa o de municipio, el id elegido contra lo anterior no coincide nunca.
                if (scope.IgnoreUnmatched)
                {
                    continue;
                }

                throw Invalid(codePrefix);
            }

            if (!string.Equals(candidate.Rate.Country, country, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(codePrefix);
            }

            rates.Add(candidate.Rate);
        }

        // El mismo id dos veces también es dos del mismo tipo.
        if (rates.GroupBy(rate => rate.Type, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new QuotationsDomainException(
                $"{codePrefix}.billing.withholding_type_repeated",
                "Only one withholding tax rate per type can be selected.");
        }

        // P6: un tipo sin id aceptado conserva lo que dejó la re-resolución, salvo que el asesor eligiera «Sin retención».
        var chosenTypes = rates.Select(rate => rate.Type).ToHashSet(StringComparer.Ordinal);
        rates.AddRange(reResolved.Where(rate =>
            !chosenTypes.Contains(rate.Type) && clearedTypes?.Contains(rate.Type) != true));
        return rates;
    }

    /// <summary>
    /// Si los ids que llegan son los congelados, en el mismo orden (D17, D24): todo guardado reenvía los selectores que
    /// tiene en pantalla, y volver a resolverlos leería el catálogo de hoy (un porcentaje editado, otra UVT) o haría 422
    /// por una tasa desactivada después. Sólo cuenta como igual si las congeladas siguen siendo del país de la empresa:
    /// si la empresa cambió de país, se resuelven y el 422 de <see cref="ResolveAsync"/> sigue en pie. Con igual, el
    /// llamador conserva las filas congeladas tal cual (Review Focus 1): así un porcentaje municipal editado sobrevive al
    /// reenvío del mismo selector.
    /// </summary>
    public static bool IsUnchanged(
        IReadOnlyCollection<Guid> ids, IEnumerable<WithholdingRate> frozen, string? companyCountry)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(frozen);
        var rates = frozen.ToList();
        var country = companyCountry?.Trim();
        return ids.SequenceEqual(rates.Select(rate => rate.TaxRateId))
            && rates.All(rate => string.Equals(rate.Country, country, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Los tipos de los ids municipales del cuerpo, coincidan o no con la tasa de la empresa (plan P12): la marca «Sin
    /// retención» no puede llegar junto con un id de su tipo. Un id que no existe no es de ningún tipo y no cuenta.
    /// </summary>
    public static async Task<IReadOnlyCollection<string>> MunicipalTypesOfAsync(
        IQuotationWithholdingLookup lookup,
        Guid tenantId,
        IReadOnlyCollection<Guid>? ids,
        DateOnly documentDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (ids is null || ids.Count == 0)
        {
            return [];
        }

        var candidates = await lookup.ForRatesAsync(tenantId, ids, documentDate, cancellationToken);
        return candidates.Values
            .Select(candidate => candidate.Rate)
            .Where(rate => TaxCountries.IsMunicipal(rate.Country, rate.Type))
            .Select(rate => rate.Type)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static QuotationsDomainException Invalid(string codePrefix) =>
        new(
            $"{codePrefix}.billing.withholding_tax_rate_invalid",
            "Every selected withholding tax rate must exist, be active, withhold and belong to the billing company's country.");
}
