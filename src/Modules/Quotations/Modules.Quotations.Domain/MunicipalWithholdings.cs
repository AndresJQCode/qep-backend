namespace Modules.Quotations.Domain;

/// <summary>Por qué un tipo municipal tiene o no fila en un documento (spec ReteICA por empresa §9.4). Se calcula al
/// leer, para explicar; no se guarda (A14).</summary>
public enum MunicipalWithholdingStatus
{
    Resolved,
    NoOperationCity,
    NoCompanyRate,
    CustomerNotAgent,
    NotSelected,
}

/// <summary>
/// Lo que hay para un tipo municipal en el municipio de la operación (plan P4): la tasa que la empresa facturadora
/// tiene asignada ahí (0 o 1, ya como foto) y si el cliente es agente retenedor de ese tipo ahí.
/// </summary>
/// <param name="Type">El tipo municipal (<c>"ReteICA"</c>).</param>
/// <param name="CompanyRate">La tasa de la empresa para ese municipio; null si no tiene.</param>
/// <param name="CustomerIsAgent">Si el cliente está registrado como agente de ese tipo en ese municipio.</param>
public sealed record MunicipalOffer(string Type, WithholdingRate? CompanyRate, bool CustomerIsAgent);

/// <summary>
/// El contexto municipal que la aplicación arma antes de llamar al agregado (plan P4): el municipio de la operación
/// que va a quedar y lo que se ofrece por cada tipo municipal del país de la empresa. El agregado no consulta: decide
/// con esto si hubo cambio y qué fila queda.
/// </summary>
/// <param name="OperationCityId">El municipio de la operación resultante; null sin empresa o en un país sin tipos
/// municipales.</param>
/// <param name="Offers">Una por tipo municipal del país de la empresa; vacía si el país no tiene.</param>
public sealed record MunicipalContext(Guid? OperationCityId, IReadOnlyList<MunicipalOffer> Offers)
{
    /// <summary>Sin municipio y sin ofertas: un documento sin empresa o de un país sin tipos municipales.</summary>
    public static MunicipalContext None { get; } = new(null, []);
}

/// <summary>
/// La resolución de un tipo municipal (spec ReteICA por empresa §7.5): una sola regla para la cotización y el pedido,
/// junto a <see cref="WithholdingCandidates"/>. Las tasas nacionales no pasan por acá.
/// </summary>
public static class MunicipalWithholdings
{
    /// <summary>
    /// Regla del cliente (fuente <c>Customer</c> de la cotización y pedido que nace sin cotización, owner R6): la tasa de
    /// la empresa para el municipio de la operación, si existe y el cliente es agente ahí. Sin municipio, ninguna.
    /// </summary>
    public static WithholdingRate? ByCustomerRule(MunicipalOffer offer, Guid? operationCityId)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return operationCityId is not null && offer.CustomerIsAgent ? offer.CompanyRate : null;
    }

    /// <summary>
    /// Regla del selector (facturación propia de la cotización y edición del pedido, owner R11) en una re-resolución: si
    /// el selector del tipo tenía tasa, pasa a la de la empresa para el municipio nuevo, sea o no agente el cliente; si
    /// estaba vacío, sigue vacío.
    /// </summary>
    /// <param name="hadSelection">Si había una fila de ese tipo antes del cambio.</param>
    public static WithholdingRate? BySelectorRule(MunicipalOffer offer, Guid? operationCityId, bool hadSelection)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return operationCityId is not null && hadSelection ? offer.CompanyRate : null;
    }

    /// <summary>
    /// El porqué de un tipo municipal, con la precedencia de §9.4: hay fila → <c>Resolved</c>; sin municipio →
    /// <c>NoOperationCity</c>; la empresa no tiene tasa ahí → <c>NoCompanyRate</c>; con facturación propia el tercero no
    /// tiene lista de agentes → <c>NotSelected</c>; si no, <c>CustomerNotAgent</c> cuando el cliente no es agente y
    /// <c>NotSelected</c> cuando lo es (o no se sabe).
    /// </summary>
    /// <param name="customerIsAgent">null si no se consultó (sin cliente o sin municipio).</param>
    public static MunicipalWithholdingStatus Describe(
        bool hasRow,
        Guid? operationCityId,
        bool companyHasRate,
        bool? customerIsAgent,
        bool ownBilling)
    {
        if (hasRow)
        {
            return MunicipalWithholdingStatus.Resolved;
        }

        if (operationCityId is null)
        {
            return MunicipalWithholdingStatus.NoOperationCity;
        }

        if (!companyHasRate)
        {
            return MunicipalWithholdingStatus.NoCompanyRate;
        }

        return !ownBilling && customerIsAgent == false
            ? MunicipalWithholdingStatus.CustomerNotAgent
            : MunicipalWithholdingStatus.NotSelected;
    }
}
