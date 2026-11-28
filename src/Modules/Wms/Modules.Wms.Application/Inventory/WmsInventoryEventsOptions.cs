namespace Modules.Wms.Application;

/// <summary>
/// La sección <c>Wms:InventoryEvents</c> de appsettings. <see cref="StartAt"/> es la marca de arranque del consumidor
/// <c>wms.inventory-events</c> (spec §5.2): sólo mira mensajes del outbox posteriores a ella, así no recorre la historia de
/// <c>created</c> y <c>cancelled</c> de todos los tenants antes del primer evento real. Es obligatoria: sin marca, el consumidor
/// leería todo el outbox. Se fija al desplegar.
/// </summary>
public sealed class WmsInventoryEventsOptions
{
    public const string SectionName = "Wms:InventoryEvents";

    public DateTimeOffset StartAt { get; init; }
}
