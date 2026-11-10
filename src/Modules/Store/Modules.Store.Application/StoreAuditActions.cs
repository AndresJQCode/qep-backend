namespace Modules.Store.Application;

/// <summary>Las acciones que el módulo publica a auditoría (spec de la tienda en línea, 2026-11-03).</summary>
public static class StoreAuditActions
{
    public const string SettingsUpdated = "store.settings.updated";

    public const string Published = "store.settings.published";

    public const string Unpublished = "store.settings.unpublished";

    public const string RequestConverted = "store.order_request.converted";

    public const string RequestDiscarded = "store.order_request.discarded";

    /// <summary>El checkout creó un pedido: anónima, sobre el id del pedido.</summary>
    public const string OrderCreated = "store.order.created";

    /// <summary>Tipos de recurso (<c>resourceType</c>) de esas acciones.</summary>
    public static class Resources
    {
        public const string Store = "store";

        public const string OrderRequest = "order_request";

        public const string Order = "order";
    }
}
