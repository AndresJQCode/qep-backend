namespace Modules.Hr.Application;

// Las respuestas de la carga anual de valores legales por Excel (spec 2026-11-22, «Carga por Excel»). Backend for frontend: la vista
// previa ya trae cada fila clasificada, con lo que había y lo que quedaría, para que la pantalla sólo la dibuje. Los enums viajan con
// su nombre y el diccionario en español lo tiene el frontend.

/// <summary>Lo que la acción de una fila o conjunto puede valer en <see cref="LegalParameterImportItemDto.Action"/>.</summary>
public static class LegalParameterImportActions
{
    /// <summary>Una fila que no existía y trae un valor distinto del que regía.</summary>
    public const string Create = "Create";

    /// <summary>Una fila que no existía y repite el valor que regía en esa fecha: el valor sigue, sólo se registra el año.</summary>
    public const string Carry = "Carry";

    /// <summary>Una fila que ya existe con otro valor.</summary>
    public const string Update = "Update";

    /// <summary>Una fila que ya existe con el mismo valor: no se escribe ni deja historial.</summary>
    public const string Unchanged = "Unchanged";

    public const string Error = "Error";
}

/// <summary>
/// La vista previa de la carga. <c>CanApply</c> es verdadero sólo sin errores: la confirmación es todo o nada. Los avisos no bloquean.
/// </summary>
public sealed record LegalParameterImportPreviewDto(
    string FileName,
    LegalParameterImportSummaryDto Summary,
    bool CanApply,
    IReadOnlyList<LegalParameterImportItemDto> Items,
    IReadOnlyList<LegalParameterImportWarningDto> Warnings);

/// <summary>Cuántos elementos (un valor o un conjunto de tramos cuenta uno) hay de cada acción.</summary>
public sealed record LegalParameterImportSummaryDto(int Create, int Carry, int Update, int Unchanged, int Error);

/// <summary>
/// Un valor (<c>Kind</c> <c>Parameter</c>, hoja «Parámetros») o un conjunto de tramos (<c>Brackets</c>, hoja «Tramos»), con todas las
/// filas de la hoja que lo forman. <c>OldValue</c>/<c>OldBrackets</c> es lo que regía en esa fecha antes de aplicar (nulo si nada);
/// <c>Code</c> y <c>Message</c> sólo viajan en un error, y el mensaje ya está en español para mostrarlo tal cual.
/// </summary>
public sealed record LegalParameterImportItemDto(
    string Kind,
    string Sheet,
    IReadOnlyList<int> Rows,
    string? Key,
    string? Name,
    DateOnly? EffectiveFrom,
    string Action,
    decimal? OldValue,
    decimal? NewValue,
    IReadOnlyList<LegalBracketDto>? OldBrackets,
    IReadOnlyList<LegalBracketDto>? NewBrackets,
    string? Code,
    string? Message);

/// <summary>Un aviso: a la clave le falta la fila del año siguiente después de aplicar la carga.</summary>
public sealed record LegalParameterImportWarningDto(string Code, string Key, string Message);

/// <summary>
/// El resultado de confirmar. <c>Created</c> son sólo las filas nuevas con valor distinto; <c>Carried</c> las que repiten el valor
/// vigente; todas comparten un lote (<c>BatchId</c>) en el historial.
/// </summary>
public sealed record LegalParameterImportResultDto(
    string FileName, DateTimeOffset ReceivedAt, Guid BatchId, int Created, int Carried, int Updated, int Unchanged);

/// <summary>La plantilla descargable: el contenido <c>.xlsx</c> y el nombre con el año que se carga.</summary>
public sealed record LegalParameterImportTemplateFile(byte[] Content, string FileName);
