using Modules.Customers.Application;

namespace Modules.Customers.UnitTests;

/// <summary>La celda «Etiquetas» del Excel: nombres separados por «;», recortados, sin vacíos ni repetidos.</summary>
public sealed class ExcelTagCellTests
{
    [Fact]
    public void ParseSplitsTrimsAndDropsEmptyAndRepeated()
    {
        var names = ExcelTagCell.Parse("Evento macel; VIP ;; vip");

        Assert.Equal(["Evento macel", "VIP"], names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("; ;")]
    public void ParseOfAnEmptyCellIsEmpty(string? cell)
    {
        Assert.Empty(ExcelTagCell.Parse(cell));
    }
}
