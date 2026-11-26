using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El interruptor del indicativo en el Excel de pedidos (spec 2026-11-25): parte del layout, encendido por
/// defecto, y con las mismas reglas de versión y no-op que el nombre de la hoja.
/// </summary>
public sealed class OrdersExportLayoutPhoneDialCodeTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static OrdersExportLayout NewLayout() => OrdersExportLayout.CreateDefault(TenantId, Now);

    private static List<OrdersExportColumnSetting> Defaults() =>
        [.. OrdersExportLayout.Effective(stored: null)];

    private static OrdersExportLayout LayoutWithoutDialCode()
    {
        var layout = NewLayout();
        Assert.True(layout.Replace(Defaults(), layout.SheetName, includePhoneDialCode: false, Later));
        return layout;
    }

    [Fact]
    public void TheDefaultLayoutIncludesTheDialCode()
    {
        Assert.True(OrdersExportLayout.DefaultIncludePhoneDialCode);
        Assert.True(NewLayout().IncludePhoneDialCode);
    }

    [Fact]
    public void WithoutAStoredLayoutTheEffectiveSwitchIsOn()
    {
        Assert.True(OrdersExportLayout.EffectiveIncludePhoneDialCode(stored: null));
    }

    [Fact]
    public void TheEffectiveSwitchOfAStoredLayoutIsItsOwn()
    {
        Assert.False(OrdersExportLayout.EffectiveIncludePhoneDialCode(LayoutWithoutDialCode()));
    }

    // Guardar sólo el interruptor es un cambio.
    [Fact]
    public void ReplaceThatOnlyFlipsTheSwitchBumpsTheVersion()
    {
        var layout = NewLayout();

        var changed = layout.Replace(Defaults(), "Pedidos", includePhoneDialCode: false, Later);

        Assert.True(changed);
        Assert.False(layout.IncludePhoneDialCode);
        Assert.Equal(2, layout.Version);
        Assert.Equal(Later, layout.UpdatedAt);
    }

    [Fact]
    public void ReplaceWithTheSameSwitchIsANoOp()
    {
        var layout = LayoutWithoutDialCode();

        var changed = layout.Replace(Defaults(), "Pedidos", includePhoneDialCode: false, Later.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(2, layout.Version);
        Assert.Equal(Later, layout.UpdatedAt);
    }

    // Las sobrecargas sin interruptor conservan el actual.
    [Fact]
    public void ReplaceWithoutTheSwitchKeepsTheCurrentOne()
    {
        var layout = LayoutWithoutDialCode();

        Assert.True(layout.Replace(Defaults(), "MIGRACION 1", Later.AddMinutes(1)));
        var columns = Defaults();
        (columns[0], columns[1]) = (columns[1], columns[0]);
        Assert.True(layout.Replace(columns, Later.AddMinutes(2)));

        Assert.False(layout.IncludePhoneDialCode);
        Assert.Equal("MIGRACION 1", layout.SheetName);
        Assert.Equal(4, layout.Version);
    }

    [Fact]
    public void ARejectedReplaceDoesNotTouchTheSwitch()
    {
        var layout = NewLayout();

        Assert.Throws<QuotationsDomainException>(
            () => layout.Replace(Defaults(), "Hoja/1", includePhoneDialCode: false, Later));

        Assert.True(layout.IncludePhoneDialCode);
        Assert.Equal(1, layout.Version);
        Assert.Equal(Now, layout.UpdatedAt);
    }
}
