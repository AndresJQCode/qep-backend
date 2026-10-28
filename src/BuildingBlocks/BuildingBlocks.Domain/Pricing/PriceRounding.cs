namespace BuildingBlocks.Domain.Pricing;

/// <summary>El sentido en que se redondea un precio a la grilla de la regla.</summary>
public enum PriceRoundingMode
{
    /// <summary>Al más cercano; la mitad se aleja de cero.</summary>
    Nearest,

    /// <summary>Hacia arriba: nunca cobra menos que el valor calculado.</summary>
    Up,

    /// <summary>Hacia abajo: nunca cobra más que el valor calculado.</summary>
    Down,
}

/// <summary>
/// La regla con que se redondea un precio unitario: cuántos decimales y en qué sentido (spec
/// 2026-10-27). Es un valor y no una moneda: quien calcula recibe la regla ya resuelta.
/// </summary>
public readonly record struct PriceRounding
{
    public PriceRounding(int decimals, PriceRoundingMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown price rounding mode.");
        }

        Decimals = decimals;
        Mode = mode;
    }

    public int Decimals { get; }

    public PriceRoundingMode Mode { get; }

    public decimal Apply(decimal value)
    {
        var factor = Pow10(Decimals);
        return Mode switch
        {
            PriceRoundingMode.Up => Math.Ceiling(value * factor) / factor,
            PriceRoundingMode.Down => Math.Floor(value * factor) / factor,
            _ => Math.Round(value, Decimals, MidpointRounding.AwayFromZero),
        };
    }

    /// <summary>
    /// Puente temporal hasta que cada documento lleve su regla: lo que hacía el código antes del
    /// spec. COP en pesos enteros y cualquier otra moneda en centavos, al más cercano. Código ISO
    /// literal porque BuildingBlocks no referencia el catálogo de monedas de Tenancy.
    /// </summary>
    public static PriceRounding LegacyFor(string currency) =>
        currency == "COP"
            ? new PriceRounding(0, PriceRoundingMode.Nearest)
            : new PriceRounding(2, PriceRoundingMode.Nearest);

    private static decimal Pow10(int decimals)
    {
        var factor = 1m;
        for (var i = 0; i < decimals; i++)
        {
            factor *= 10m;
        }

        return factor;
    }
}
