namespace Modules.Hr.Domain;

public enum LegalParameterChangeAction
{
    Created,
    Updated,
    Deleted,
}

public enum LegalParameterChangeSource
{
    Manual,
    Excel,
}

/// <summary>
/// Una entrada del historial de cambios de los valores legales (spec 2026-11-22, «Datos»): sólo se inserta, nunca se edita. Dato de la
/// plataforma, sin <c>tenant_id</c>. <see cref="Key"/> es el nombre de un <see cref="LegalParameterKey"/> o de un
/// <see cref="LegalBracketKey"/>: no hay columna de tipo, porque los nombres no se repiten entre los dos enums. Los valores van como
/// JSON: el número (<c>1750905</c>) o la lista de tramos.
/// </summary>
public sealed class LegalParameterChange
{
    // Los largos de las columnas `changed_by_name` y `file_name`: un texto más largo haría fallar el guardado con un 500, y el historial
    // no es lugar para rechazar un cambio ya validado, así que se recortan.
    public const int ChangedByNameMaxLength = 200;
    public const int FileNameMaxLength = 255;

    // EF Core materializa por acá.
    private LegalParameterChange()
    {
    }

    private LegalParameterChange(
        string key,
        DateOnly effectiveFrom,
        LegalParameterChangeAction action,
        string? oldValue,
        string? newValue,
        Guid changedBy,
        string changedByName,
        DateTimeOffset changedAt,
        LegalParameterChangeSource source,
        Guid? batchId,
        string? fileName)
    {
        Id = Guid.CreateVersion7();
        Key = key;
        EffectiveFrom = effectiveFrom;
        Action = action;
        OldValue = oldValue;
        NewValue = newValue;
        ChangedBy = changedBy;
        ChangedByName = Truncate(changedByName, ChangedByNameMaxLength);
        ChangedAt = changedAt;
        Source = source;
        BatchId = batchId;
        FileName = fileName is null ? null : Truncate(fileName, FileNameMaxLength);
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public DateOnly EffectiveFrom { get; private set; }

    public LegalParameterChangeAction Action { get; private set; }

    public string? OldValue { get; private set; }

    public string? NewValue { get; private set; }

    /// <summary>El <c>SubjectId</c> del usuario que cambió: el operador actúa desde su tenant, pero el registro es de la plataforma.</summary>
    public Guid ChangedBy { get; private set; }

    /// <summary>El nombre que tenía su membresía al escribir («Sin nombre» si no tenía).</summary>
    public string ChangedByName { get; private set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; private set; }

    public LegalParameterChangeSource Source { get; private set; }

    /// <summary>El lote de una carga por Excel; <c>null</c> en lo manual.</summary>
    public Guid? BatchId { get; private set; }

    /// <summary>El nombre del archivo cargado; sólo en Excel.</summary>
    public string? FileName { get; private set; }

    public static LegalParameterChange Record(
        string key,
        DateOnly effectiveFrom,
        LegalParameterChangeAction action,
        string? oldValueJson,
        string? newValueJson,
        Guid changedBy,
        string changedByName,
        DateTimeOffset changedAt,
        LegalParameterChangeSource source,
        Guid? batchId,
        string? fileName) =>
        new(key, effectiveFrom, action, oldValueJson, newValueJson, changedBy, changedByName, changedAt, source, batchId, fileName);

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];
}
