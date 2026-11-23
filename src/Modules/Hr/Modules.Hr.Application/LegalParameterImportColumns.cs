namespace Modules.Hr.Application;

/// <summary>
/// Las hojas y los encabezados del Excel de valores legales (spec 2026-11-22, «Carga por Excel»). Las dos hojas llevan la clave como
/// texto del enum y la fecha como <c>yyyy-MM-dd</c>; el lector ubica cada columna por su encabezado, sin mayúsculas ni tildes.
/// </summary>
public static class LegalParameterImportColumns
{
    public const string ParametersSheet = "Parámetros";
    public const string BracketsSheet = "Tramos";

    public const string Key = "Clave";
    public const string Name = "Nombre";
    public const string CurrentValue = "Valor vigente";
    public const string CurrentFrom = "Vigente desde";
    public const string EffectiveFrom = "Rige desde";
    public const string NewValue = "Valor nuevo";

    public const string LowerBound = "Desde";
    public const string UpperBound = "Hasta";
    public const string Rate = "Tasa";

    /// <summary>Filas con datos entre las dos hojas.</summary>
    public const int MaxRows = 500;

    public const long MaxSizeInBytes = 1 * 1024 * 1024;

    /// <summary>Las columnas que el lector exige en «Parámetros»; las demás son de lectura y se ignoran.</summary>
    public static IReadOnlyList<string> RequiredParameterColumns { get; } = [Key, EffectiveFrom, NewValue];

    /// <summary>Las columnas que el lector exige en «Tramos».</summary>
    public static IReadOnlyList<string> RequiredBracketColumns { get; } = [Key, EffectiveFrom, LowerBound, UpperBound, Rate];

    public static string TemplateFileName(int year) => $"parametros-legales-{year}.xlsx";
}
