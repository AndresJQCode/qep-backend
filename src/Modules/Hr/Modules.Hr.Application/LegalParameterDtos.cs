namespace Modules.Hr.Application;

// Las respuestas de la consola de valores legales del operador (spec 2026-11-22, «Forma de las respuestas»). Backend for frontend:
// la pantalla no calcula nada. Los enums viajan con su nombre (Smmlv, Currency, Parameter): el diccionario en español lo tiene el
// frontend.

/// <summary>
/// La pantalla entera en una respuesta; el PUT y el DELETE devuelven este mismo DTO para que el frontend lo escriba en su caché sin
/// recomponer <c>InUse</c> ni <c>Current</c>. «Hoy» sale del calendario del tenant operador: el navegador no usa su reloj. Los
/// conteos los calcula el backend (28 = 27 claves anuales más el conjunto de tramos): el navegador no cuenta.
/// </summary>
public sealed record LegalParametersDto(
    DateOnly Today,
    int CurrentYear,
    int CurrentYearMissingCount,
    int NextYear,
    int NextYearMissingCount,
    IReadOnlyList<LegalParameterKeyDto> Parameters,
    IReadOnlyList<LegalBracketKeyDto> Brackets);

/// <summary>
/// Una clave con todas sus filas, de la vigencia más reciente a la más vieja. Nombre, grupo y unidad vienen de
/// <c>LegalParameterLabels</c> para que la pantalla no repita el diccionario; <c>Current</c> es la fila que rige hoy (nula si ninguna).
/// <c>CurrentYearMissing</c> y <c>NextYearMissing</c> son falsos en las claves que no son anuales.
/// </summary>
public sealed record LegalParameterKeyDto(
    string Key,
    string Name,
    string Group,
    string Unit,
    bool Yearly,
    bool NextYearMissing,
    bool CurrentYearMissing,
    LegalParameterCurrentDto? Current,
    IReadOnlyList<LegalParameterRowDto> Rows);

public sealed record LegalParameterCurrentDto(DateOnly EffectiveFrom, decimal Value);

/// <summary><c>InUse</c> dice si una nómina ya calculada cae en el rango de la fila: la pantalla apaga editar y borrar sin esperar el 409. <c>UpdatedAt</c> es nulo en las filas sembradas por migración.</summary>
public sealed record LegalParameterRowDto(DateOnly EffectiveFrom, decimal Value, bool InUse, DateTimeOffset? UpdatedAt);

/// <summary>Los tramos van aparte de las claves escalares: se editan como un conjunto entero por vigencia.</summary>
public sealed record LegalBracketKeyDto(
    string Key,
    string Name,
    bool NextYearMissing,
    bool CurrentYearMissing,
    LegalBracketCurrentDto? Current,
    IReadOnlyList<LegalBracketSetDto> Sets);

public sealed record LegalBracketCurrentDto(DateOnly EffectiveFrom);

/// <summary><c>UpdatedAt</c> del conjunto es el mayor de sus tramos: el conjunto se reemplaza entero, así que la pantalla muestra una sola fecha.</summary>
public sealed record LegalBracketSetDto(
    DateOnly EffectiveFrom,
    bool InUse,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<LegalBracketDto> Brackets);

/// <summary>Límites en múltiplos de SMMLV; <c>UpperBound</c> nulo en el último tramo. Lo reutiliza el historial y la vista previa del Excel.</summary>
public sealed record LegalBracketDto(decimal LowerBound, decimal? UpperBound, decimal Rate);

/// <summary>El historial paginado, como <c>OperatorHistoryPageDto</c> de Tenancy.</summary>
public sealed record LegalParameterHistoryPageDto(
    IReadOnlyList<LegalParameterChangeDto> Items, int Total, int Page, int PageSize);

/// <summary>
/// Un cambio con valores tipados: un <c>Parameter</c> lleva <c>OldValue</c>/<c>NewValue</c> como número y un <c>Brackets</c> lleva
/// <c>OldBrackets</c>/<c>NewBrackets</c> como lista, para que el frontend nunca parsee el JSON de la base. Nulos en «Creó» (lo
/// anterior) y en «Borró» (lo nuevo).
/// </summary>
public sealed record LegalParameterChangeDto(
    Guid Id,
    string Kind,
    string Key,
    DateOnly EffectiveFrom,
    string Action,
    decimal? OldValue,
    decimal? NewValue,
    IReadOnlyList<LegalBracketDto>? OldBrackets,
    IReadOnlyList<LegalBracketDto>? NewBrackets,
    DateTimeOffset ChangedAt,
    LegalParameterActorDto ChangedBy,
    string Source,
    Guid? BatchId,
    string? FileName);

/// <summary>El usuario con el nombre que tenía su membresía al cambiar: el historial no consulta a Tenancy para pintarlo.</summary>
public sealed record LegalParameterActorDto(Guid SubjectId, string DisplayName);
