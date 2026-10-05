using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El nombre de la hoja del Excel de pedidos (spec 2026-10-05): parte del layout (D1), con default
/// <c>Pedidos</c> (D3), las reglas de Excel en el dominio (D4) y un no-op que mira columnas y
/// nombre (D5).
/// </summary>
public sealed class OrdersExportLayoutSheetNameTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static OrdersExportLayout NewLayout() => OrdersExportLayout.CreateDefault(TenantId, Now);

    private static List<OrdersExportColumnSetting> Defaults() =>
        [.. OrdersExportLayout.Effective(stored: null)];

    [Fact]
    public void TheDefaultSheetNameIsPedidos()
    {
        Assert.Equal("Pedidos", OrdersExportLayout.DefaultSheetName);
        Assert.Equal(31, OrdersExportLayout.SheetNameMaxLength);
        Assert.Equal("Pedidos", NewLayout().SheetName);
    }

    // D3: sin fila guardada, el efectivo también es el default.
    [Fact]
    public void WithoutAStoredLayoutTheEffectiveSheetNameIsTheDefault()
    {
        Assert.Equal("Pedidos", OrdersExportLayout.EffectiveSheetName(stored: null));
    }

    [Fact]
    public void TheEffectiveSheetNameOfAStoredLayoutIsItsOwn()
    {
        var layout = NewLayout();
        Assert.True(layout.Replace(Defaults(), "MIGRACION 1", Later));

        Assert.Equal("MIGRACION 1", OrdersExportLayout.EffectiveSheetName(layout));
    }

    // D5: guardar sólo el nombre es un cambio. Antes el no-op miraba sólo columnas.
    [Fact]
    public void ReplaceThatOnlyRenamesTheSheetBumpsTheVersion()
    {
        var layout = NewLayout();

        var changed = layout.Replace(Defaults(), "MIGRACION 1", Later);

        Assert.True(changed);
        Assert.Equal("MIGRACION 1", layout.SheetName);
        Assert.Equal(2, layout.Version);
        Assert.Equal(Later, layout.UpdatedAt);
    }

    [Fact]
    public void ReplaceWithTheSameColumnsAndSheetNameIsANoOp()
    {
        var layout = NewLayout();

        var changed = layout.Replace(Defaults(), "Pedidos", Later);

        Assert.False(changed);
        Assert.Equal(1, layout.Version);
        Assert.Equal(Now, layout.UpdatedAt);
    }

    // Mismo criterio que el encabezado: se recortan los extremos, y lo recortado es lo que se
    // compara. "  Pedidos  " no es un cambio.
    [Fact]
    public void TheSheetNameIsTrimmedBeforeComparing()
    {
        var layout = NewLayout();

        Assert.False(layout.Replace(Defaults(), "  Pedidos  ", Later));
        Assert.True(layout.Replace(Defaults(), "  MIGRACION 1 ", Later));
        Assert.Equal("MIGRACION 1", layout.SheetName);
    }

    // Ordinal: "pedidos" es un cambio, como "Empresa" y "EMPRESA" en un encabezado.
    [Fact]
    public void ASheetNameThatDiffersOnlyByCaseIsAChange()
    {
        var layout = NewLayout();

        Assert.True(layout.Replace(Defaults(), "pedidos", Later));
        Assert.Equal("pedidos", layout.SheetName);
    }

    // La sobrecarga sin nombre conserva el actual: es lo que hace el handler con un PUT sin
    // `sheetName` (D6).
    [Fact]
    public void ReplaceWithoutASheetNameKeepsTheCurrentOne()
    {
        var layout = NewLayout();
        Assert.True(layout.Replace(Defaults(), "MIGRACION 1", Later));
        var columns = Defaults();
        (columns[0], columns[1]) = (columns[1], columns[0]);

        Assert.True(layout.Replace(columns, Later.AddMinutes(1)));

        Assert.Equal("MIGRACION 1", layout.SheetName);
        Assert.Equal(3, layout.Version);
    }

    // Las tildes son válidas: Excel las acepta (D4).
    [Theory]
    [InlineData("MIGRACION 1")]
    [InlineData("Migración")]
    [InlineData("A")]
    [InlineData("1234567890123456789012345678901")]
    [InlineData("Hoja's")]
    [InlineData("Historia")]
    [InlineData("History 1")]
    public void ValidSheetNamesAreAccepted(string sheetName)
    {
        var layout = NewLayout();

        Assert.True(OrdersExportLayout.IsValidSheetName(sheetName));
        Assert.True(layout.Replace(Defaults(), sheetName, Later));
        Assert.Equal(sheetName, layout.SheetName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678901234567890123456789012")]
    [InlineData("Hoja[1]")]
    [InlineData("Hoja]")]
    [InlineData("Hoja:1")]
    [InlineData("Hoja*")]
    [InlineData("Hoja?")]
    [InlineData("Hoja/1")]
    [InlineData("Hoja\\1")]
    [InlineData("'Hoja")]
    [InlineData("Hoja'")]
    [InlineData("History")]
    [InlineData("history")]
    [InlineData("  HISTORY ")]
    public void InvalidSheetNamesAreRejectedWithoutTouchingTheLayout(string sheetName)
    {
        var layout = NewLayout();

        Assert.False(OrdersExportLayout.IsValidSheetName(sheetName));
        var error = Assert.Throws<QuotationsDomainException>(
            () => layout.Replace(Defaults(), sheetName, Later));

        Assert.Equal("quotations.orders_export_layout.sheet_name_invalid", error.Code);
        Assert.Equal("Pedidos", layout.SheetName);
        Assert.Equal(1, layout.Version);
        Assert.Equal(Now, layout.UpdatedAt);
    }

    // El largo se mide recortado: 31 caracteres con espacios alrededor siguen siendo 31.
    [Fact]
    public void TheLengthIsMeasuredAfterTrimming()
    {
        Assert.True(OrdersExportLayout.IsValidSheetName("  1234567890123456789012345678901  "));
    }

    [Fact]
    public void NullIsNotAValidSheetName()
    {
        Assert.False(OrdersExportLayout.IsValidSheetName(null));
    }
}
