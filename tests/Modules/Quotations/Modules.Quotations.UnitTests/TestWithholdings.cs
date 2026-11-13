using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Tasas de retención congeladas para armar documentos en las pruebas.</summary>
internal static class TestWithholdings
{
    /// <summary>La retención de siempre: Retefuente 2,5 % de Colombia, sin base mínima.</summary>
    public static WithholdingRate RetefuenteCo { get; } = new(
        Guid.Parse("0193b6a2-0000-7000-8000-000000000025"), "Retefuente 2,5 %", "Retefuente", "CO", 2.5m, null, null, null);

    /// <summary>La foto de un cliente que retiene (Retefuente 2,5 %) o que no retiene.</summary>
    public static IReadOnlyList<WithholdingRate> Customer(bool withRetention) => withRetention ? [RetefuenteCo] : [];
}
