namespace Modules.Hr.Domain;

/// <summary>
/// Un valor legal desde una fecha. Dato de la plataforma: no tiene <c>tenant_id</c> a propósito, igual que
/// <c>catalog.tax_unit_values</c>. Sin pantalla: lo siembra una migración por año.
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
}
