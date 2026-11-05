using Modules.Geography.Application;

namespace Modules.Geography.UnitTests;

/// <summary>Compara un grupo del impacto por valor. <see cref="ImpactCountDto"/> es un record con una lista adentro, y la
/// igualdad del record compara esa lista por referencia: un <c>Assert.Equal</c> entre dos grupos idénticos falla siempre.</summary>
internal static class ImpactAssert
{
    public static void Equal(ImpactCountDto expected, ImpactCountDto actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.ByTenant, actual.ByTenant);
    }
}
