using BuildingBlocks.Domain.Catalog;

namespace BuildingBlocks.Domain.Taxes;

/// <summary>A qué líneas aplica una tasa de retención (spec 2026-11-18 §4.2). Viaja por nombre. Sólo vale distinto de
/// <see cref="All"/> en tipos con <see cref="TaxBehavior.Withholds"/>.</summary>
public enum WithholdingScope
{
    All,
    Goods,
    Services,
}

/// <summary>Las reglas del alcance de una retención, en un solo lugar para el cálculo y para los selectores.</summary>
public static class WithholdingScopes
{
    /// <summary>Si una línea de ese tipo entra en la base de una tasa con ese alcance.</summary>
    public static bool Covers(WithholdingScope scope, ProductKind kind) => scope switch
    {
        WithholdingScope.All => true,
        WithholdingScope.Goods => kind == ProductKind.Physical,
        WithholdingScope.Services => kind == ProductKind.Service,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Alcance de retención desconocido."),
    };

    /// <summary>Si dos tasas del mismo tipo retendrían dos veces sobre las mismas líneas: sólo <c>Goods</c> y
    /// <c>Services</c> conviven (spec §4.3).</summary>
    public static bool Overlap(WithholdingScope a, WithholdingScope b) =>
        a == WithholdingScope.All || b == WithholdingScope.All || a == b;
}
