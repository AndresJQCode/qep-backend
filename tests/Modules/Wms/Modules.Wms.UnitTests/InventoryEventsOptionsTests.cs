using Modules.Wms.Application;
using Modules.Wms.Infrastructure;

namespace Modules.Wms.UnitTests;

/// <summary>Decisión del owner, 2026-11-27: la marca de arranque del consumidor de inventario no tiene valor por defecto en producción.
/// Un arranque sin ella falla con un mensaje que nombra la variable de entorno que hay que poner.</summary>
public sealed class InventoryEventsOptionsTests
{
    [Fact]
    public void WithoutAStartMarkTheOptionsAreInvalidAndTheMessageNamesTheKey()
    {
        var result = new WmsInventoryEventsOptionsValidator().Validate(null, new WmsInventoryEventsOptions());

        Assert.True(result.Failed);
        Assert.Contains("Wms__InventoryEvents__StartAt", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Wms:InventoryEvents:StartAt", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void WithAStartMarkTheOptionsAreValid()
    {
        var options = new WmsInventoryEventsOptions { StartAt = new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero) };

        Assert.True(new WmsInventoryEventsOptionsValidator().Validate(null, options).Succeeded);
    }
}
