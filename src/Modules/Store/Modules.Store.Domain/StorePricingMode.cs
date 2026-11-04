namespace Modules.Store.Domain;

/// <summary>«Sólo detal» y «Con escalas por cantidad»; uno para toda la tienda, no por moneda.</summary>
public enum StorePricingMode
{
    RetailOnly,
    QuantityScales,
}

public static class StorePricingModes
{
    /// <summary>
    /// Único mapeo de modo a <c>isRetail</c>. El piso global de escala es siempre null: en la
    /// tienda nadie elige un piso.
    /// </summary>
    public static bool IsRetail(StorePricingMode mode) => mode == StorePricingMode.RetailOnly;
}
