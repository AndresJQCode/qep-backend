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

    /// <summary>
    /// Si algún par de un grupo de tasas del mismo tipo se solapa (<see cref="Overlap"/>): es la regla «una por tipo y
    /// alcance» de los cuatro selectores (spec §4.3), cada uno con su agrupador y su código.
    /// </summary>
    public static bool AnyOverlap(IReadOnlyList<WithholdingScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        for (var i = 0; i < scopes.Count; i++)
        {
            for (var j = i + 1; j < scopes.Count; j++)
            {
                if (Overlap(scopes[i], scopes[j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// La regla de repetición de un grupo de tasas del mismo tipo (spec §4.3). Un tipo municipal (ReteICA) sigue con una
    /// sola tasa, cualquiera sea su alcance: la edición del porcentaje y la re-resolución por ciudad identifican la fila
    /// por tipo, y dos filas del mismo tipo las volverían ambiguas. Un tipo nacional admite una de bienes y una de
    /// servicios (<see cref="AnyOverlap"/>).
    /// </summary>
    public static bool Repeated(IReadOnlyList<WithholdingScope> scopes, bool municipal)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return municipal ? scopes.Count > 1 : AnyOverlap(scopes);
    }
}
