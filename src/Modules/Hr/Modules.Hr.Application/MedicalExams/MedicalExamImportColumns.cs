namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// The eight exact headers of the medical exam history template, in their order (spec 2026-11-23 §8). The reader finds them by name,
/// without case or accents; the rest of the code uses these constants, never a position.
/// </summary>
public static class MedicalExamImportColumns
{
    public const string DataSheetName = "Exámenes";

    public const string ValuesSheetName = "Valores válidos";

    public const string TemplateFileName = "plantilla-examenes-medicos.xlsx";

    public const int MaxRows = 1000;

    public const long MaxSizeInBytes = 5 * 1024 * 1024;

    public const string DocumentType = "TIPO DOCUMENTO";
    public const string DocumentNumber = "NÚMERO DOCUMENTO";
    public const string ExamType = "TIPO DE EXAMEN";
    public const string ExamDate = "FECHA DEL EXAMEN";
    public const string Provider = "IPS / MÉDICO";
    public const string Restrictions = "RESTRICCIONES";
    public const string Recommendations = "RECOMENDACIONES";
    public const string RestrictionsUntil = "RESTRICCIONES HASTA";

    public static readonly IReadOnlyList<string> Ordered =
    [
        DocumentType, DocumentNumber, ExamType, ExamDate, Provider, Restrictions, Recommendations, RestrictionsUntil,
    ];

    /// <summary>The titles of the columns of «Valores válidos»: one per list.</summary>
    public static class ValuesTitles
    {
        public const string DocumentTypes = "Tipos de documento";
        public const string ExamTypes = "Tipos de examen";
    }
}
