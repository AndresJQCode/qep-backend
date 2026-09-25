using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// D8: lo que ven GET y el processor es lo guardado en su orden, más toda llave del catálogo que
/// no esté guardada, al final, visible y con su nombre por defecto; una llave guardada que ya no
/// existe se descarta en silencio. Función pura sobre un catálogo de prueba: así se ejercen "llave
/// nueva" y "llave que se fue" sin tocar el real.
/// </summary>
public sealed class OrdersExportLayoutEffectiveTests
{
    private static readonly IReadOnlyList<OrdersExportCatalogColumn> Catalog =
    [
        new("company", "EMPRESA", 30),
        new("email", "Email", 30),
        new("city", "Ciudad", 20),
    ];

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithoutAStoredLayoutTheEffectiveIsTheCatalogAsIs()
    {
        var effective = OrdersExportLayout.Effective([], Catalog);

        Assert.Equal(["company", "email", "city"], effective.Select(column => column.Key));
        Assert.Equal(["EMPRESA", "Email", "Ciudad"], effective.Select(column => column.Header));
        Assert.All(effective, column => Assert.True(column.Visible));
        Assert.All(effective, column => Assert.Equal(OrdersExportColumnKind.Catalog, column.Kind));
    }

    // Sin fila, contra el catálogo real: las 35 en su orden, con sus nombres — el Excel de hoy.
    [Fact]
    public void WithoutAStoredLayoutTheRealCatalogComesOutWhole()
    {
        var effective = OrdersExportLayout.Effective(stored: null);

        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Select(column => column.Key),
            effective.Select(column => column.Key));
        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Select(column => column.DefaultHeader),
            effective.Select(column => column.Header));
    }

    [Fact]
    public void AStoredColumnKeepsItsHeaderOrderAndVisibility()
    {
        var stored = new[]
        {
            OrdersExportColumnSetting.Catalog("email", "Correo", visible: true),
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: false),
            OrdersExportColumnSetting.Catalog("city", "Ciudad", visible: true),
        };

        var effective = OrdersExportLayout.Effective(stored, Catalog);

        Assert.Equal(["email", "company", "city"], effective.Select(column => column.Key));
        Assert.Equal("Correo", effective[0].Header);
        Assert.False(effective[1].Visible);
    }

    // Una columna que el backend suma después del guardado aparece sola, sin re-guardar.
    [Fact]
    public void ANewCatalogKeyIsAppendedVisibleWithItsDefaultHeader()
    {
        var stored = new[]
        {
            OrdersExportColumnSetting.Catalog("city", "Ciudad", visible: true),
            OrdersExportColumnSetting.Catalog("company", "Empresa", visible: true),
        };

        var effective = OrdersExportLayout.Effective(stored, Catalog);

        Assert.Equal(["city", "company", "email"], effective.Select(column => column.Key));
        Assert.Equal("Email", effective[2].Header);
        Assert.True(effective[2].Visible);
    }

    [Fact]
    public void AStoredKeyThatLeftTheCatalogIsDropped()
    {
        var stored = new[]
        {
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true),
            OrdersExportColumnSetting.Catalog("fax", "Fax", visible: true),
            OrdersExportColumnSetting.Catalog("email", "Email", visible: true),
        };

        var effective = OrdersExportLayout.Effective(stored, Catalog);

        Assert.Equal(["company", "email", "city"], effective.Select(column => column.Key));
    }

    // Ajuste 2026-09-25: una llave puede viajar bajo varios encabezados. Toda entrada guardada con
    // llave conocida se conserva, repetidas incluidas y en su orden; al final sólo se completa lo
    // que no aparece ni una vez.
    [Fact]
    public void RepeatedStoredKeysAreAllKeptAndNotCompletedAgain()
    {
        var stored = new[]
        {
            OrdersExportColumnSetting.Catalog("email", "Correo", visible: true),
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true),
            OrdersExportColumnSetting.Catalog("email", "Correo 2", visible: false),
        };

        var effective = OrdersExportLayout.Effective(stored, Catalog);

        Assert.Equal(["email", "company", "email", "city"], effective.Select(column => column.Key));
        Assert.Equal(["Correo", "EMPRESA", "Correo 2", "Ciudad"], effective.Select(column => column.Header));
        Assert.False(effective[2].Visible);
    }

    // La fija no tiene llave: la identifica su posición, y la conserva.
    [Fact]
    public void FixedColumnsKeepTheirPosition()
    {
        var stored = new[]
        {
            OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true),
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true),
            OrdersExportColumnSetting.Fixed("Bodega", "01", visible: true),
        };

        var effective = OrdersExportLayout.Effective(stored, Catalog);

        Assert.Equal(OrdersExportColumnKind.Fixed, effective[0].Kind);
        Assert.Equal("Tipo Doc", effective[0].Header);
        Assert.Equal("FV", effective[0].Value);
        Assert.Equal("company", effective[1].Key);
        Assert.Equal("Bodega", effective[2].Header);
        Assert.Equal(["email", "city"], effective.Skip(3).Select(column => column.Key));
    }

    // D9: el layout que todo tenant tiene sin haber guardado nada, en versión 1.
    [Fact]
    public void CreateDefaultIsTheCatalogAtVersionOne()
    {
        var tenantId = Guid.CreateVersion7();

        var layout = OrdersExportLayout.CreateDefault(tenantId, Now);

        Assert.Equal(tenantId, layout.TenantId);
        Assert.Equal(OrdersExportLayout.DefaultVersion, layout.Version);
        Assert.Equal(1, layout.Version);
        Assert.Equal(Now, layout.UpdatedAt);
        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Select(column => column.Key),
            layout.Columns.Select(column => column.Key));
    }

    // Las factorías normalizan y validan lo que no necesita mirar la lista entera.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankHeaderIsHeaderInvalid(string? header)
    {
        var error = Assert.Throws<QuotationsDomainException>(
            () => OrdersExportColumnSetting.Catalog("company", header, visible: true));

        Assert.Equal("quotations.orders_export_layout.header_invalid", error.Code);
    }

    [Fact]
    public void AHeaderLongerThanSixtyFourIsHeaderInvalidAndExactlySixtyFourIsAccepted()
    {
        var error = Assert.Throws<QuotationsDomainException>(
            () => OrdersExportColumnSetting.Fixed(new string('h', 65), "x", visible: true));
        Assert.Equal("quotations.orders_export_layout.header_invalid", error.Code);

        var accepted = OrdersExportColumnSetting.Fixed(new string('h', 64), "x", visible: true);
        Assert.Equal(64, accepted.Header.Length);
    }

    // Review Focus 1: se recortan los extremos y nada más.
    [Fact]
    public void AHeaderKeepsItsInnerSpacesAndOnlyTrimsTheEnds()
    {
        var column = OrdersExportColumnSetting.Catalog("product_code", "  Cod.  Producto ", visible: true);

        Assert.Equal("Cod.  Producto", column.Header);
    }

    [Fact]
    public void AFixedValueLongerThan128IsFixedValueInvalid()
    {
        var error = Assert.Throws<QuotationsDomainException>(
            () => OrdersExportColumnSetting.Fixed("Bodega", new string('v', 129), visible: true));

        Assert.Equal("quotations.orders_export_layout.fixed_value_invalid", error.Code);
    }

    // D4: vacío es válido — un ERP puede exigir la columna aunque venga en blanco.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyFixedValueIsValidAndStoredEmpty(string? value)
    {
        var column = OrdersExportColumnSetting.Fixed("Bodega", value, visible: true);

        Assert.Equal(string.Empty, column.Value);
    }

    // Review Focus 3: sólo espacios se guarda vacío, no como espacios.
    [Fact]
    public void AFixedValueOfOnlySpacesIsStoredEmpty()
    {
        var column = OrdersExportColumnSetting.Fixed("Bodega", "   ", visible: true);

        Assert.Equal(string.Empty, column.Value);
    }

    [Fact]
    public void ACatalogColumnHasNoValueAndAFixedOneHasNoKey()
    {
        var catalog = OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true);
        var fixedColumn = OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true);

        Assert.Null(catalog.Value);
        Assert.Null(fixedColumn.Key);
    }

    [Fact]
    public void IsSameAsComparesEveryFieldOrdinally()
    {
        var column = OrdersExportColumnSetting.Catalog("company", "Empresa", visible: true);

        Assert.True(column.IsSameAs(OrdersExportColumnSetting.Catalog("company", "Empresa", visible: true)));
        Assert.False(column.IsSameAs(OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true)));
        Assert.False(column.IsSameAs(OrdersExportColumnSetting.Catalog("company", "Empresa", visible: false)));
        Assert.False(column.IsSameAs(OrdersExportColumnSetting.Fixed("Empresa", "", visible: true)));
    }
}
