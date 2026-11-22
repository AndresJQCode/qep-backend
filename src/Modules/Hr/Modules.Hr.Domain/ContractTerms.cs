namespace Modules.Hr.Domain;

/// <summary>
/// Las condiciones de un contrato. <c>CompanyId</c> es la empresa empleadora (FK a <c>companies.companies</c>, D-11): Application
/// ya comprobó que existe, que está activa si es un vínculo nuevo (D-12) y que el centro es de una sede suya.
/// </summary>
public sealed record ContractTerms(
    Guid CompanyId,
    ContractType ContractType,
    ContractDuration Duration,
    SalaryType SalaryType,
    DateOnly StartDate,
    DateOnly? EndDate);
