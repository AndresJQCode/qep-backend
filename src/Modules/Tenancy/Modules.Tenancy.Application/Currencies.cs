using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// A currency the product can price, quote and sell in. Symbol and decimals are fixed per currency
/// (spec D8): the tenant number format only decides the separators.
/// </summary>
public sealed record CurrencyInfo(string Code, string Symbol, int Decimals);

/// <summary>
/// The global currency catalogue (spec 2026-10-08, D1). Adding a currency is one line in
/// <see cref="All"/>: no migration, no enum and no frontend deploy, because the frontend reads the
/// list from <c>GET /auth/me</c>. There is no conversion between currencies anywhere.
///
/// Lives in Tenancy.Application because it is the only business assembly every other module's
/// Application may reference. Domain assemblies cannot see it, so they only guard the shape of a
/// code; the catalogue check belongs to the application layer of each module.
/// </summary>
public static class Currencies
{
    public const string UnsupportedCode = "tenancy.currency.unsupported";

    public static IReadOnlyList<CurrencyInfo> All { get; } =
    [
        new("COP", "$", 0),
        new("USD", "US$", 2),
        new("EUR", "€", 2),
    ];

    // Declared after All on purpose: static initialisers run in textual order.
    private static readonly Dictionary<string, CurrencyInfo> ByCode =
        All.ToDictionary(currency => currency.Code, StringComparer.Ordinal);

    public static bool IsSupported(string? code) =>
        code is not null && ByCode.ContainsKey(code.Trim().ToUpperInvariant());

    public static string Normalize(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        return normalized is not null && ByCode.ContainsKey(normalized)
            ? normalized
            : throw new TenantDomainException(UnsupportedCode, $"Currency '{code}' is not supported.");
    }

    public static CurrencyInfo Get(string code) => ByCode[Normalize(code)];

    /// <summary>Position in the catalogue, for "catalogue order" sorting (spec D10, Excel and
    /// report columns). An unknown code sorts last instead of throwing: it only orders data that
    /// already exists.</summary>
    public static int OrderOf(string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        for (var index = 0; index < All.Count; index++)
        {
            if (All[index].Code == normalized)
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}
