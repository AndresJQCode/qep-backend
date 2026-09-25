using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las reglas de <see cref="OrdersExportLayout.Replace"/> (spec 2026-09-24, "Dominio"), una por
/// código de error, sobre el catálogo real. Toda regla se revisa antes de tocar la lista: un
/// rechazo deja columnas, versión y fecha como estaban.
/// </summary>
public sealed class OrdersExportLayoutTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static OrdersExportLayout NewLayout() => OrdersExportLayout.CreateDefault(TenantId, Now);

    /// <summary>Las 33 del catálogo con sus nombres, visibles, como lista editable.</summary>
    private static List<OrdersExportColumnSetting> Defaults() =>
        [.. OrdersExportLayout.Effective(stored: null)];

    private static OrdersExportColumnSetting Catalog(string key, string header, bool visible = true) =>
        OrdersExportColumnSetting.Catalog(key, header, visible);

    private static OrdersExportColumnSetting Fixed(string header, string value, bool visible = true) =>
        OrdersExportColumnSetting.Fixed(header, value, visible);

    private static void AssertRejected(OrdersExportLayout layout, IReadOnlyList<OrdersExportColumnSetting> columns, string code)
    {
        var before = layout.Columns.ToArray();
        var version = layout.Version;
        var updatedAt = layout.UpdatedAt;

        var error = Assert.Throws<QuotationsDomainException>(() => layout.Replace(columns, Later));

        Assert.Equal(code, error.Code);
        Assert.Equal(before, layout.Columns);
        Assert.Equal(version, layout.Version);
        Assert.Equal(updatedAt, layout.UpdatedAt);
    }

    // Guardar sin tocar no consume versión: subirla daría un 412 falso en otra pestaña abierta.
    [Fact]
    public void ReplaceWithTheSameColumnsIsANoOp()
    {
        var layout = NewLayout();

        var changed = layout.Replace(Defaults(), Later);

        Assert.False(changed);
        Assert.Equal(1, layout.Version);
        Assert.Equal(Now, layout.UpdatedAt);
    }

    [Fact]
    public void ReplaceThatOnlyReordersBumpsTheVersion()
    {
        var layout = NewLayout();
        var columns = Defaults();
        (columns[0], columns[1]) = (columns[1], columns[0]);

        var changed = layout.Replace(columns, Later);

        Assert.True(changed);
        Assert.Equal(2, layout.Version);
        Assert.Equal(Later, layout.UpdatedAt);
        Assert.Equal("product_code", layout.Columns[0].Key);
        Assert.Equal("company", layout.Columns[1].Key);
    }

    [Fact]
    public void ReplaceThatOnlyRenamesOrHidesBumpsTheVersion()
    {
        var layout = NewLayout();
        var columns = Defaults();
        columns[18] = Catalog("email", "Correo");
        columns[0] = Catalog("company", "EMPRESA", visible: false);

        Assert.True(layout.Replace(columns, Later));
        Assert.Equal("Correo", layout.Columns[18].Header);
        Assert.False(layout.Columns[0].Visible);
        Assert.Equal(2, layout.Version);
    }

    // Hallazgo 5: se guarda la lista completada con el catálogo, así que un PUT parcial deja las
    // llaves omitidas al final, visibles y con su nombre.
    [Fact]
    public void ReplaceStoresTheListCompletedWithTheCatalog()
    {
        var layout = NewLayout();

        var changed = layout.Replace([Catalog("email", "Correo"), Fixed("Tipo Doc", "FV")], Later);

        Assert.True(changed);
        Assert.Equal(34, layout.Columns.Count);
        Assert.Equal("email", layout.Columns[0].Key);
        Assert.Equal(OrdersExportColumnKind.Fixed, layout.Columns[1].Kind);
        Assert.Equal("company", layout.Columns[2].Key);
        Assert.Equal("EMPRESA", layout.Columns[2].Header);
        Assert.True(layout.Columns[2].Visible);
    }

    // Y por lo mismo, mandar sólo las llaves que ya están en su lugar por defecto no es un cambio.
    [Fact]
    public void APartialListThatCompletesToTheStoredOneIsANoOp()
    {
        var layout = NewLayout();

        var changed = layout.Replace([Catalog("company", "EMPRESA")], Later);

        Assert.False(changed);
        Assert.Equal(1, layout.Version);
    }

    [Fact]
    public void ReplaceRejectsAnUnknownKey()
    {
        var columns = Defaults();
        columns.Add(Catalog("fax", "Fax"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.columns_invalid");
    }

    // Review Focus 5: la llave es un identificador, ordinal.
    [Fact]
    public void ReplaceRejectsAKeyThatDiffersOnlyByCase()
    {
        var columns = Defaults();
        columns[0] = Catalog("Company", "EMPRESA");

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.columns_invalid");
    }

    [Fact]
    public void ReplaceRejectsARepeatedKey()
    {
        var columns = Defaults();
        columns.Add(Catalog("email", "Otro correo"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.columns_invalid");
    }

    // El ERP lee por encabezado: dos visibles iguales se pisan. Sin distinguir mayúsculas.
    [Fact]
    public void TwoVisibleColumnsWithTheSameHeaderIgnoringCaseAreRejected()
    {
        var columns = Defaults();
        columns[18] = Catalog("email", "ciudad");

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.header_duplicated");
    }

    [Fact]
    public void AVisibleFixedColumnCannotRepeatAVisibleCatalogHeader()
    {
        var columns = Defaults();
        columns.Insert(0, Fixed("Email", "x"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.header_duplicated");
    }

    // Hallazgo 5: la regla mira la lista efectiva. Una fija "Email" con la llave `email` omitida
    // chocaría con la que el catálogo completa al final.
    [Fact]
    public void AVisibleFixedHeaderThatMatchesAnOmittedCatalogColumnIsRejected()
    {
        var columns = Defaults().Where(column => column.Key != "email").ToList();
        columns.Add(Fixed("EMAIL", "x"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.header_duplicated");
    }

    // Entre ocultas puede repetirse, y una oculta puede repetir a una visible: no viajan.
    [Fact]
    public void HiddenColumnsMayRepeatAHeader()
    {
        var layout = NewLayout();
        var columns = Defaults();
        columns[0] = Catalog("company", "Ciudad", visible: false);
        columns[1] = Catalog("product_code", "Ciudad", visible: false);

        Assert.True(layout.Replace(columns, Later));
        Assert.Equal(2, layout.Version);
    }

    [Fact]
    public void HidingEveryColumnIsRejected()
    {
        var columns = Defaults()
            .Select(column => Catalog(column.Key!, column.Header, visible: false))
            .ToList();

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.all_hidden");
    }

    [Fact]
    public void TenFixedColumnsAreAccepted()
    {
        var layout = NewLayout();
        var columns = Defaults();
        columns.AddRange(Enumerable.Range(1, 10).Select(number => Fixed($"Fija {number}", $"{number}")));

        Assert.True(layout.Replace(columns, Later));
        Assert.Equal(10, layout.Columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed));
    }

    [Fact]
    public void ElevenFixedColumnsAreRejected()
    {
        var columns = Defaults();
        columns.AddRange(Enumerable.Range(1, 11).Select(number => Fixed($"Fija {number}", $"{number}")));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.too_many_fixed_columns");
    }

    // Review Focus 4: el rechazo no toca la fila que ya tenía sus diez.
    [Fact]
    public void AnEleventhFixedColumnOnAStoredLayoutLeavesItIntact()
    {
        var layout = NewLayout();
        var ten = Defaults();
        ten.AddRange(Enumerable.Range(1, 10).Select(number => Fixed($"Fija {number}", $"{number}")));
        layout.Replace(ten, Later);
        var eleven = layout.Columns.ToList();
        eleven.Add(Fixed("Fija 11", "11"));

        AssertRejected(layout, eleven, "quotations.orders_export_layout.too_many_fixed_columns");

        Assert.Equal(2, layout.Version);
        Assert.Equal(10, layout.Columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed));
    }

    // Las fijas ocultas también cuentan para el tope: son entradas que la pantalla lista.
    [Fact]
    public void HiddenFixedColumnsCountTowardsTheLimit()
    {
        var columns = Defaults();
        columns.AddRange(Enumerable.Range(1, 11).Select(number => Fixed($"Fija {number}", $"{number}", visible: false)));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.too_many_fixed_columns");
    }

    // Las reglas se evalúan en este orden: llaves, tope de fijas, alguna visible, duplicados. Un
    // cuerpo con dos fallas responde la primera, y la pantalla no ve una regla cambiar de lugar.
    [Fact]
    public void AnUnknownKeyWinsOverADuplicatedHeader()
    {
        var columns = Defaults();
        columns[18] = Catalog("email", "Ciudad");
        columns.Add(Catalog("fax", "Fax"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.columns_invalid");
    }
}
