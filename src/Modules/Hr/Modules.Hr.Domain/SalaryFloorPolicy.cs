namespace Modules.Hr.Domain;

/// <summary>Las reglas del salario contra los pisos legales (plan 2026-11-20, «Reglas del salario»).</summary>
public static class SalaryFloorPolicy
{
    /// <summary>
    /// Los tipos de contrato que pueden quedar por debajo del mínimo sin confirmar (spec, FI-EMP-018). <c>Employee</c> y
    /// <c>UniversityIntern</c> exigen confirmación.
    /// </summary>
    public static IReadOnlySet<ContractType> BelowMinimumAllowed { get; } = new HashSet<ContractType>
    {
        ContractType.Contractor,
        ContractType.PartTime,
        ContractType.SenaApprenticeTeaching,
        ContractType.SenaApprenticeProductive,
        ContractType.SenaApprenticeDual,
        ContractType.StudentLaw789,
        ContractType.StudentDecree055,
    };

    /// <summary>
    /// Devuelve <c>belowMinimumAcknowledged</c>: <c>true</c> si el monto queda bajo el mínimo en un tipo no exento y el usuario
    /// lo confirmó. El integral bajo su piso se rechaza siempre, aun confirmado.
    /// </summary>
    public static bool Check(ContractType type, SalaryType salaryType, decimal amount, SalaryFloor floor, bool acknowledged)
    {
        if (salaryType == SalaryType.Integral && amount < floor.IntegralFloor)
        {
            throw new HrDomainException(
                HrErrorCodes.BelowIntegralFloor,
                "An integral salary cannot be below the integral salary floor in force on the record date.");
        }

        var belowMinimum = amount < floor.Smmlv && !BelowMinimumAllowed.Contains(type);
        if (belowMinimum && !acknowledged)
        {
            throw new HrDomainException(
                HrErrorCodes.BelowMinimumUnacknowledged,
                "The salary is below the legal minimum and the subject has not acknowledged it.");
        }

        return belowMinimum;
    }
}
