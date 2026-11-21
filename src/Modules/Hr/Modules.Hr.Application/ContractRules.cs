using BuildingBlocks.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Reglas de contrato que viven en Application porque necesitan lo cargado (la sede del centro, el contrato del empleado) o
/// devuelven un error de campo en vez de un código de dominio. Los chequeos de campo van <b>antes</b> de mutar: el dominio lanza
/// excepciones de argumento donde el contrato HTTP promete un 422 sobre el campo.
/// </summary>
internal static class ContractRules
{
    private static readonly HashSet<string> NotFoundCodes = new(StringComparer.Ordinal)
    {
        HrErrorCodes.ContractNotFound,
        HrErrorCodes.SalaryNotFound,
        HrErrorCodes.AssignmentNotFound,
        HrErrorCodes.RenewalNotFound,
    };

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

    // Los contratos, registros y prórrogas son del empleado ya cargado (y acotado a su tenant): uno que no está ahí es 404.
    public static EmploymentContract FindContract(Employee employee, Guid contractId) =>
        employee.Contracts.FirstOrDefault(contract => contract.Id.Value == contractId) ?? throw HrNotFound.Contract(contractId);

    public static SalaryRecord FindSalary(EmploymentContract contract, Guid salaryId) =>
        contract.Salaries.FirstOrDefault(record => record.Id.Value == salaryId) ?? throw HrNotFound.Salary(salaryId);

    public static AssignmentRecord FindAssignment(EmploymentContract contract, Guid assignmentId) =>
        contract.Assignments.FirstOrDefault(record => record.Id.Value == assignmentId) ?? throw HrNotFound.Assignment(assignmentId);

    public static ContractRenewal FindRenewal(EmploymentContract contract, Guid renewalId) =>
        contract.Renewals.FirstOrDefault(renewal => renewal.Id.Value == renewalId) ?? throw HrNotFound.Renewal(renewalId);

    /// <summary>Sólo el contrato vigente admite escrituras de contrato; el agregado lo repite, pero acá se dice antes de los campos.</summary>
    public static void EnsureActive(EmploymentContract contract)
    {
        if (contract.Status != ContractStatus.Active)
        {
            throw new HrDomainException(HrErrorCodes.ContractNotActive, "The contract is not active.");
        }
    }

    /// <summary>Un registro que rige después del último día del contrato quedaría colgando: 422 sobre <c>effectiveDate</c>.</summary>
    public static void EnsureEffectiveDateWithinContract(EmploymentContract contract, DateOnly effectiveDate)
    {
        if (contract.EndDate is { } end && effectiveDate > end)
        {
            HrFieldErrors.Throw("effectiveDate", "La fecha no puede ser posterior a la terminación del contrato.");
        }
    }

    public static void EnsureNewEndDateAfterCurrent(EmploymentContract contract, DateOnly newEndDate)
    {
        if (contract.EndDate is { } end && newEndDate <= end)
        {
            HrFieldErrors.Throw("newEndDate", "La nueva terminación tiene que ser posterior a la actual.");
        }
    }

    public static void EnsureDateNotBeforeStart(EmploymentContract contract, DateOnly date)
    {
        if (date < contract.StartDate)
        {
            HrFieldErrors.Throw("date", "La fecha no puede ser anterior al inicio del contrato.");
        }
    }

    /// <summary>
    /// Corre una mutación del agregado: un conflicto de estado es 409 (<see cref="HrConflicts"/>) y un «no existe» del dominio es 404.
    /// </summary>
    public static T Run<T>(Func<T> mutation)
    {
        try
        {
            return HrConflicts.Run(mutation);
        }
        catch (HrDomainException exception) when (NotFoundCodes.Contains(exception.Code))
        {
            throw new ResourceNotFoundException(exception.Code, exception.Message);
        }
    }

    public static void Run(Action mutation) => Run(() =>
    {
        mutation();
        return true;
    });
}
