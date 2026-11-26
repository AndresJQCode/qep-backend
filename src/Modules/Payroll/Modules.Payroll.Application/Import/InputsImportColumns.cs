namespace Modules.Payroll.Application;

/// <summary>
/// Las columnas y los nombres de la importación de novedades (contrato HTTP, «Importación de novedades»). Los encabezados se
/// reconocen sin mayúsculas ni tildes; <see cref="Employee"/> es informativa y no se lee.
/// </summary>
public static class InputsImportColumns
{
    public const string DocumentNumber = "NÚMERO DE DOCUMENTO";
    public const string Employee = "EMPLEADO";
    public const string Concept = "CONCEPTO";
    public const string Hours = "HORAS";
    public const string Amount = "VALOR";
    public const string SalaryConstitutive = "CONSTITUTIVO DE SALARIO";

    public const string DataSheetName = "Novedades";
    public const string EmployeesSheetName = "Empleados";
    public const string ValuesSheetName = "Valores válidos";
    public const string TemplateFileName = "plantilla-novedades.xlsx";

    public const string ConceptsListTitle = "Conceptos";
    public const string YesNoListTitle = "Sí / No";

    public const int MaxRows = 5000;
    public const long MaxSizeInBytes = 5 * 1024 * 1024;

    /// <summary>En el orden de la hoja <c>Novedades</c>.</summary>
    public static IReadOnlyList<string> Ordered { get; } =
        [DocumentNumber, Employee, Concept, Hours, Amount, SalaryConstitutive];

    /// <summary>Las que el lector necesita: <see cref="Employee"/> es sólo para quien llena el archivo.</summary>
    public static IReadOnlyList<string> Read { get; } = [DocumentNumber, Concept, Hours, Amount, SalaryConstitutive];
}
