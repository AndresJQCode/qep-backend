namespace Modules.Hr.Domain;

/// <summary>
/// Un valor legal desde una fecha. Dato de la plataforma: no tiene <c>tenant_id</c> a propósito, igual que
/// <c>catalog.tax_unit_values</c>. Lo siembra una migración por año y lo edita el operador desde su consola (spec 2026-11-22):
/// <see cref="UpdatedAt"/> y <see cref="UpdatedBy"/> son <c>null</c> mientras nadie lo haya tocado desde la consola.
/// </summary>
public sealed class LegalParameterValue
{
    // EF Core materializa por acá.
    private LegalParameterValue()
    {
    }

    public LegalParameterValue(LegalParameterKey key, DateOnly effectiveFrom, decimal value)
    {
        Key = key;
        EffectiveFrom = effectiveFrom;
        Value = value;
    }

    public LegalParameterKey Key { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public decimal Value { get; private set; }

    /// <summary>Cuándo se escribió o cambió por última vez desde la consola; <c>null</c> en las filas sembradas por migración.</summary>
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>El usuario (<c>IExecutionContext.SubjectId</c>) que la escribió o cambió; <c>null</c> en las filas sembradas.</summary>
    public Guid? UpdatedBy { get; private set; }

    /// <summary>Una fila nueva escrita desde la consola: nace con su autor.</summary>
    public static LegalParameterValue Create(
        LegalParameterKey key, DateOnly effectiveFrom, decimal value, Guid by, DateTimeOffset at) =>
        new(key, effectiveFrom, value) { UpdatedAt = at, UpdatedBy = by };

    /// <summary>Cambia el valor de la fila y deja constancia de quién y cuándo.</summary>
    public void Replace(decimal value, Guid by, DateTimeOffset at)
    {
        Value = value;
        UpdatedAt = at;
        UpdatedBy = by;
    }
}
