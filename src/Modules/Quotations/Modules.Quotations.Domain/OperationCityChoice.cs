namespace Modules.Quotations.Domain;

/// <summary>Qué dijo el cuerpo sobre el municipio de la operación (plan P1, spec ReteICA por empresa §9.4).</summary>
public enum OperationCityChoiceKind
{
    /// <summary>El campo no vino: se conserva el guardado, salvo que cambie la empresa (A6).</summary>
    Keep,

    /// <summary>Vino <c>null</c>: «el de la empresa».</summary>
    Company,

    /// <summary>Vino un municipio.</summary>
    Explicit,
}

/// <summary>
/// La elección del municipio de la operación en un cuerpo: ausente, <c>null</c> o un id. La aplicación la arma desde el
/// JSON y el agregado la traduce al municipio resultante (<c>OperationCityFor</c>, <c>OperationCityAtCreation</c>).
/// </summary>
public readonly record struct OperationCityChoice(OperationCityChoiceKind Kind, Guid? CityId)
{
    public static OperationCityChoice Keep { get; } = new(OperationCityChoiceKind.Keep, null);

    public static OperationCityChoice Company { get; } = new(OperationCityChoiceKind.Company, null);

    public static OperationCityChoice Explicit(Guid cityId) => new(OperationCityChoiceKind.Explicit, cityId);

    /// <summary>
    /// El municipio de un documento que estrena empresa (o que todavía no tiene): sin empresa o en un país sin tipos
    /// municipales no hay municipio (A7); explícito, el elegido; si no, el de la empresa (A6). Una sola regla para la
    /// cotización y el pedido.
    /// </summary>
    internal Guid? ForNewCompany(QuotationBillingAccount? billingAccount) =>
        billingAccount is null || !MunicipalReResolver.HasMunicipalTypes(billingAccount.Country)
            ? null
            : Kind == OperationCityChoiceKind.Explicit ? CityId : billingAccount.CityId;

    /// <summary>
    /// El municipio resultante para un documento que ya tiene empresa y municipio guardados (A6): si cambia la empresa,
    /// como uno nuevo; si no, explícito el elegido, <c>null</c> el de la empresa y ausente el guardado.
    /// </summary>
    internal Guid? For(
        QuotationBillingAccount? billingAccount, Guid? currentCompanyId, Guid? currentOperationCityId)
    {
        if (billingAccount is null || !MunicipalReResolver.HasMunicipalTypes(billingAccount.Country))
        {
            return null;
        }

        if (billingAccount.CompanyId != currentCompanyId)
        {
            return ForNewCompany(billingAccount);
        }

        return Kind switch
        {
            OperationCityChoiceKind.Explicit => CityId,
            OperationCityChoiceKind.Company => billingAccount.CityId,
            _ => currentOperationCityId,
        };
    }
}
