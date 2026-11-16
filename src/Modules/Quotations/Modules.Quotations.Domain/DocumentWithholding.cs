namespace Modules.Quotations.Domain;

/// <summary>De dónde salió una tasa congelada: del cliente o de los selectores de facturación propia
/// (cotización), o del pedido, que tiene una sola fuente.</summary>
public enum WithholdingSource
{
    Customer,
    Billing,
    Document,
}

/// <summary>
/// Fila hija de una cotización o un pedido: una tasa de retención congelada y lo que dio en el último
/// recálculo. Se guardan también las que no aplican (con <see cref="Applied"/> en false), porque la tasa
/// puede empezar a aplicar cuando cambian las líneas, la empresa o el consumidor final.
///
/// La tasa se guarda en columnas propias de la fila y <see cref="Rate"/> se arma al leer: así reemplazar
/// la tasa de una posición es cambiar valores de una fila ya rastreada, nunca cambiar la instancia de un
/// owned anidado (que EF tendría que traducir a borrar e insertar con la misma clave).
/// </summary>
public sealed class DocumentWithholding
{
    // EF Core materializa por acá.
    private DocumentWithholding()
    {
        Name = string.Empty;
        Type = string.Empty;
        Country = string.Empty;
    }

    internal DocumentWithholding(WithholdingSource source, short position, WithholdingRate rate)
        : this()
    {
        Source = source;
        Position = position;
        Replace(rate);
    }

    public WithholdingSource Source { get; private set; }

    /// <summary>El orden de llegada dentro de su fuente.</summary>
    public short Position { get; private set; }

    public Guid TaxRateId { get; private set; }

    public string Name { get; private set; }

    public string Type { get; private set; }

    public string Country { get; private set; }

    public decimal Percentage { get; private set; }

    public decimal? MinimumBase { get; private set; }

    public string? MinimumBaseUnit { get; private set; }

    public decimal? TaxUnitValue { get; private set; }

    /// <summary>El año de <see cref="TaxUnitValue"/> (spec retenciones por UVT §5.2); null sin unidad o en una fila
    /// legada que no casó con ningún año cargado.</summary>
    public int? TaxUnitYear { get; private set; }

    /// <summary>El municipio de una tasa municipal; null en las nacionales (spec ReteICA por empresa §7.5).</summary>
    public Guid? CityId { get; private set; }

    /// <summary>El porcentaje del catálogo al congelar; <see cref="Percentage"/> es el efectivo.</summary>
    public decimal CatalogPercentage { get; private set; }

    /// <summary>Si el asesor editó el porcentaje en este documento.</summary>
    public bool Overridden => Percentage != CatalogPercentage;

    /// <summary>La tasa congelada de esta fila, con el porcentaje efectivo y el del catálogo.</summary>
    public WithholdingRate Rate =>
        new(TaxRateId, Name, Type, Country, Percentage, MinimumBase, MinimumBaseUnit, TaxUnitValue, CityId)
        {
            CatalogPercentage = CatalogPercentage,
            TaxUnitYear = TaxUnitYear,
        };

    public decimal BaseAmount { get; private set; }

    /// <summary>0 en las que no aplican.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Distingue «no aplica» de una tasa al 0 % que sí aplicó (plan P3).</summary>
    public bool Applied { get; private set; }

    /// <summary>Pone otra tasa en esta misma posición. Lo que dio el cálculo anterior ya no vale: queda
    /// sin aplicar hasta el próximo recálculo, que el agregado corre siempre después.</summary>
    internal void Replace(WithholdingRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        TaxRateId = rate.TaxRateId;
        Name = rate.Name;
        Type = rate.Type;
        Country = rate.Country;
        Percentage = rate.Percentage;
        MinimumBase = rate.MinimumBase;
        MinimumBaseUnit = rate.MinimumBaseUnit;
        TaxUnitValue = rate.TaxUnitValue;
        TaxUnitYear = rate.TaxUnitYear;
        CityId = rate.CityId;
        CatalogPercentage = rate.CatalogPercentage;
        Settle(null);
    }

    /// <summary>
    /// Pone la UVT vigente en una fila con base mínima en unidades (spec retenciones por UVT §5.4); null = no hay
    /// ninguna cargada y la tasa deja de aplicar. Las filas sin base o con base en dinero no se tocan. Como en
    /// <see cref="Replace"/>, el cálculo anterior deja de valer hasta el próximo recálculo del agregado.
    /// </summary>
    internal void RefreshTaxUnit(TaxUnitPhoto? taxUnit)
    {
        if (MinimumBase is null || MinimumBaseUnit is null)
        {
            return;
        }

        TaxUnitValue = taxUnit?.Value;
        TaxUnitYear = taxUnit?.Year;
        Settle(null);
    }

    /// <summary>
    /// <see cref="RefreshTaxUnit"/> sobre todas las filas de un documento, de todas las fuentes: la que no es la vigente
    /// también puede volver a serlo. La clave es el país canónico de la fila; un país ausente deja la fila sin unidad.
    /// </summary>
    internal static void RefreshTaxUnits(
        IEnumerable<DocumentWithholding> rows,
        IReadOnlyDictionary<string, TaxUnitPhoto> byCountry)
    {
        ArgumentNullException.ThrowIfNull(byCountry);
        foreach (var row in rows)
        {
            row.RefreshTaxUnit(byCountry.TryGetValue(row.Country, out var taxUnit) ? taxUnit : null);
        }
    }

    /// <summary>Cambia el porcentaje efectivo de la fila (spec §7.7); el del catálogo no se mueve. Como en
    /// <see cref="Replace"/>, el cálculo anterior deja de valer hasta el próximo recálculo del agregado.</summary>
    internal void SetPercentage(decimal percentage)
    {
        Percentage = percentage;
        Settle(null);
    }

    /// <summary>Guarda lo que dio el cálculo para esta tasa; null si no aplicó.</summary>
    internal void Settle(AppliedWithholding? applied)
    {
        if (applied is { } result && result.Rate != Rate)
        {
            throw new InvalidOperationException("The applied withholding belongs to another tax rate.");
        }

        Applied = applied is not null;
        BaseAmount = applied?.Base ?? 0m;
        Amount = applied?.Amount ?? 0m;
    }

    /// <summary>
    /// Reemplaza las tasas de una fuente de un documento conservando las filas que ya existen en su posición
    /// (cambian de valores, no de identidad): borrar y volver a agregar la misma clave (documento, fuente, posición)
    /// en un agregado ya rastreado no es algo que EF traduzca bien. La posición es el orden de llegada. Lo usan la
    /// cotización y el pedido.
    /// </summary>
    internal static void ReplaceSource(
        List<DocumentWithholding> rows,
        WithholdingSource source,
        IReadOnlyList<WithholdingRate> rates)
    {
        var existing = rows.Where(row => row.Source == source).OrderBy(row => row.Position).ToList();
        for (var position = 0; position < rates.Count; position++)
        {
            if (position < existing.Count)
            {
                existing[position].Replace(rates[position]);
            }
            else
            {
                rows.Add(new DocumentWithholding(source, (short)position, rates[position]));
            }
        }

        foreach (var extra in existing.Skip(rates.Count))
        {
            rows.Remove(extra);
        }
    }

    /// <summary>
    /// Liquida todas las filas de un documento con lo que devolvió <c>DocumentTotals.Compute</c>. Las de la
    /// fuente vigente se emparejan por <see cref="TaxRateId"/> y no por posición: el cálculo sólo devuelve
    /// las que aplicaron, así que las posiciones no coinciden en cuanto una no aplica. Una tasa aparece a lo
    /// sumo una vez por fuente (una por tipo), así que el id basta. Las de la otra fuente quedan sin aplicar.
    /// </summary>
    internal static void SettleAll(
        IEnumerable<DocumentWithholding> rows,
        WithholdingSource activeSource,
        IReadOnlyList<AppliedWithholding> applied)
    {
        var byRate = applied.ToDictionary(result => result.Rate.TaxRateId);
        foreach (var row in rows)
        {
            row.Settle(row.Source == activeSource && byRate.TryGetValue(row.TaxRateId, out var result)
                ? result
                : null);
        }
    }
}
