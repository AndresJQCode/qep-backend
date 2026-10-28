using BuildingBlocks.Domain.Pricing;

namespace Modules.Tenancy.Domain;

/// <summary>
/// La regla de redondeo de precios de un tenant para una moneda (spec 2026-10-27). Una moneda sin
/// fila usa el valor por defecto del catálogo, así que la fila sólo existe cuando el tenant se
/// aparta de él. El tope de decimales por moneda no se valida acá: Domain no ve el catálogo
/// (<c>Currencies</c>), mismo criterio que <c>Tenant.NormalizeCurrencyCode</c>.
/// </summary>
public sealed class TenantPriceRounding
{
    public const int CurrencyMaxLength = 3;
    public const int ModeMaxLength = 16;

    private TenantPriceRounding()
    {
        Currency = string.Empty;
    }

    private TenantPriceRounding(TenantId tenantId, string currency, PriceRounding rule, DateTimeOffset updatedAt)
    {
        TenantId = tenantId;
        Currency = currency;
        Decimals = rule.Decimals;
        Mode = rule.Mode;
        UpdatedAt = updatedAt;
    }

    public TenantId TenantId { get; private set; }

    public string Currency { get; private set; }

    public int Decimals { get; private set; }

    public PriceRoundingMode Mode { get; private set; }

    /// <summary>La regla como valor; EF persiste <see cref="Decimals"/> y <see cref="Mode"/>.</summary>
    public PriceRounding Rule => new(Decimals, Mode);

    public DateTimeOffset UpdatedAt { get; private set; }

    public static TenantPriceRounding Create(
        TenantId tenantId, string currency, int decimals, PriceRoundingMode mode, DateTimeOffset now) =>
        new(tenantId, NormalizeCurrency(currency), BuildRule(decimals, mode), now);

    /// <summary>Cambia la regla y devuelve si algo cambió; sin cambio no toca <see cref="UpdatedAt"/>.</summary>
    public bool Change(int decimals, PriceRoundingMode mode, DateTimeOffset now)
    {
        var rule = BuildRule(decimals, mode);
        if (rule == Rule)
        {
            return false;
        }

        Decimals = rule.Decimals;
        Mode = rule.Mode;
        UpdatedAt = now;
        return true;
    }

    private static PriceRounding BuildRule(int decimals, PriceRoundingMode mode)
    {
        if (decimals < 0)
        {
            throw new TenantDomainException(
                "tenancy.price_rounding.invalid",
                "Price rounding decimals cannot be negative.");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new TenantDomainException(
                "tenancy.price_rounding.invalid",
                $"Price rounding mode '{mode}' is not supported.");
        }

        return new PriceRounding(decimals, mode);
    }

    private static string NormalizeCurrency(string currency)
    {
        var normalized = currency?.Trim().ToUpperInvariant();
        if (normalized is not { Length: CurrencyMaxLength })
        {
            throw new TenantDomainException(
                "tenancy.price_rounding.invalid",
                "Price rounding currency must be a three-letter code.");
        }

        return normalized;
    }
}
