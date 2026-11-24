using BuildingBlocks.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

// Las búsquedas siempre están acotadas al tenant del llamador, así que «no encontrado» significa «no está entre tus datos».
// Uno de otro tenant no llega hasta acá: la autorización lo frena antes con 403.
internal static class HrNotFound
{
    public static ResourceNotFoundException Employee(Guid id) =>
        new(HrErrorCodes.EmployeeNotFound, $"Employee '{id}' was not found.");

    public static ResourceNotFoundException Contract(Guid id) =>
        new(HrErrorCodes.ContractNotFound, $"Contract '{id}' was not found.");

    public static ResourceNotFoundException Salary(Guid id) =>
        new(HrErrorCodes.SalaryNotFound, $"Salary record '{id}' was not found.");

    public static ResourceNotFoundException Assignment(Guid id) =>
        new(HrErrorCodes.AssignmentNotFound, $"Assignment record '{id}' was not found.");

    public static ResourceNotFoundException Renewal(Guid id) =>
        new(HrErrorCodes.RenewalNotFound, $"Renewal '{id}' was not found.");

    public static ResourceNotFoundException Site(Guid id) =>
        new(HrErrorCodes.SiteNotFound, $"Site '{id}' was not found.");

    public static ResourceNotFoundException Workplace(Guid id) =>
        new(HrErrorCodes.WorkplaceNotFound, $"Workplace '{id}' was not found.");

    public static ResourceNotFoundException Area(Guid id) =>
        new(HrErrorCodes.AreaNotFound, $"Area '{id}' was not found.");

    public static ResourceNotFoundException Position(Guid id) =>
        new(HrErrorCodes.PositionNotFound, $"Position '{id}' was not found.");

    public static ResourceNotFoundException MedicalExam(Guid id) =>
        new(HrErrorCodes.MedicalExamNotFound, $"Medical exam '{id}' was not found.");

    public static ResourceNotFoundException File(Guid id) =>
        new(HrErrorCodes.FileNotFound, $"File '{id}' was not found.");
}
