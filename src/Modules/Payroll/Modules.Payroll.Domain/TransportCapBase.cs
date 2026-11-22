namespace Modules.Payroll.Domain;

/// <summary>Con qué se compara el tope de 2 SMMLV del auxilio de transporte.</summary>
public enum TransportCapBase
{
    SalaryOnly,
    SalaryAndSalaryIncome,
    SalaryAndPaidAbsences,
}
