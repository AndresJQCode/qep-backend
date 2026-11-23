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
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsDigit(value.Trim()[0])
            && Enum.TryParse(value.Trim(), ignoreCase: true, out mode)
            && Enum.IsDefined(mode);
    }
}