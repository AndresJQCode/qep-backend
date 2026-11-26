namespace Modules.Siigo.Domain;

/// <summary>Cómo viaja el precio de cada ítem a Siigo (spec 2026-11-23). El total de la factura es el mismo en los dos modos.</summary>
public enum SiigoPriceMode
{
    /// <summary>El precio de lista y el descuento aparte (<c>discount</c>). Es el modo de siempre.</summary>
    BaseWithDiscount,

    /// <summary>El precio ya descontado y <c>discount: 0</c>.</summary>
    DiscountedPrice,
}

/// <summary>Lee el nombre del enum que llega por HTTP: sin distinguir mayúsculas y sin aceptar el número (<c>"1"</c>).</summary>
public static class SiigoPriceModes
{
    public static bool TryParse(string? value, out SiigoPriceMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Sólo el nombre exacto: Enum.TryParse también acepta «+1» y «BaseWithDiscount,DiscountedPrice» (que suma los dos valores).
        var name = Enum.GetNames<SiigoPriceMode>()
            .FirstOrDefault(candidate => string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return false;
        }

        mode = Enum.Parse<SiigoPriceMode>(name);
        return true;
    }
}
