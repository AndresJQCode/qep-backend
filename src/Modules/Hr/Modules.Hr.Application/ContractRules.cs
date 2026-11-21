using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Reglas de contrato que viven en Application porque necesitan lo cargado (la sede del centro) o devuelven un error de campo en vez
/// de un código de dominio.
/// </summary>
internal static class ContractRules
{
    /// <summary>
    /// La empresa del contrato es fija y manda sobre el lugar: un centro de una sede de otra empresa es 422
    /// <c>hr.assignment.workplace_other_company</c>, con el error en el campo del centro para que el formulario lo marque.
    /// </summary>
    public static void EnsureWorkplaceOfCompany(Site site, Guid companyId, string field)
    {
        if (site.CompanyId != companyId)
        {
            throw new HrDomainException(
                HrErrorCodes.WorkplaceOtherCompany,
                "El centro de trabajo es de otra empresa.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [field] = ["Elige un centro de trabajo de la empresa del contrato."],
                });
        }
    }
}
