namespace Modules.Payroll.Domain;

/// <summary>Por qué un empleado no se liquida en una quincena (el motor no sabe calcularlo todavía). Un excluido nunca bloquea.</summary>
public enum ExclusionReason
{
    ContractTypeNotSupported,
    SalaryTypeNotSupported,
    ContributorSubtypeNotSupported,
    ForeignPensionExempt,
    SalaryBelowMinimum,
    MultipleContractsInPeriod,
}
