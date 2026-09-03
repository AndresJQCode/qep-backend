using Modules.Reporting.Application;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// La normalizacion de paginacion. Es la unica defensa contra un <c>?pageSize=1000000</c> escrito
/// desde la barra de direcciones, y contra un <c>?page=0</c> que en una consulta con
/// <c>Skip((page - 1) * pageSize)</c> se traduce en un salto negativo.
/// </summary>
public sealed class ReportPagingTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void NormalizePageFloorsAtOne(int requested, int expected) =>
        Assert.Equal(expected, ReportPaging.NormalizePage(requested));

    [Theory]
    [InlineData(0, ReportPaging.DefaultPageSize)]
    [InlineData(-1, ReportPaging.DefaultPageSize)]
    [InlineData(25, 25)]
    [InlineData(ReportPaging.MaxPageSize, ReportPaging.MaxPageSize)]
    [InlineData(1_000_000, ReportPaging.MaxPageSize)]
    public void NormalizePageSizeClampsToTheAllowedRange(int requested, int expected) =>
        Assert.Equal(expected, ReportPaging.NormalizePageSize(requested));

    /// <summary>Los mismos numeros que el resto de los modulos. Si alguien los cambia acá, el
    /// contrato de API con el frontend deja de ser cierto.</summary>
    [Fact]
    public void TheDefaultsMatchTheContract()
    {
        Assert.Equal(50, ReportPaging.DefaultPageSize);
        Assert.Equal(200, ReportPaging.MaxPageSize);
    }
}
