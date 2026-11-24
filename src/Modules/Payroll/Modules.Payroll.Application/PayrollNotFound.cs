using BuildingBlocks.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

// Las búsquedas siempre están acotadas al tenant del llamador, así que «no encontrado» significa «no está entre tus datos»: el id
// de otro tenant pedido con la ruta del propio responde lo mismo que un id inventado, y no confirma que exista en otro lado.
internal static class PayrollNotFound
{
    public static ResourceNotFoundException Company(Guid id) =>
        new(PayrollErrorCodes.CompanyNotFound, $"Company '{id}' was not found.");

    public static ResourceNotFoundException Period(Guid id) =>
        new(PayrollErrorCodes.PeriodNotFound, $"Payroll period '{id}' was not found.");

    public static ResourceNotFoundException Employee(Guid id) =>
        new(PayrollErrorCodes.EmployeeNotFound, $"Employee '{id}' was not found.");

    public static ResourceNotFoundException EmployeeNotInPeriod(Guid id) =>
        new(PayrollErrorCodes.EmployeeNotInPeriod, $"Employee '{id}' is not part of this payroll period.");

    public static ResourceNotFoundException Concept(Guid id) =>
        new(PayrollErrorCodes.ConceptNotFound, $"Payroll concept '{id}' was not found.");

    public static ResourceNotFoundException Recurring(Guid id) =>
        new(PayrollErrorCodes.RecurringNotFound, $"Recurring concept '{id}' was not found.");

    public static ResourceNotFoundException Vacation(Guid id) =>
        new(PayrollErrorCodes.VacationNotFound, $"Vacation '{id}' was not found.");
}
