namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.1 y §5.5: los topics que llegan en <c>X-Shopify-Topic</c>, en la forma REST. Los cinco primeros los registra
/// el enlace por tienda (<c>ShopifyWebhookTopics.Registered</c> de Integrations, que este módulo no ve); los tres de cumplimiento
/// los configura el owner en la app. Si cambia la lista de Integrations, cambia acá: lo amarra
/// <c>ShopifyWebhookTopicsParityTests</c> en <c>Bootstrapper.UnitTests</c>, que ve los dos módulos.
/// </summary>
public static class ShopifyWebhookTopics
{
    public const string OrdersCreate = "orders/create";
    public const string OrdersUpdated = "orders/updated";
    public const string OrdersPaid = "orders/paid";
    public const string OrdersCancelled = "orders/cancelled";
    public const string AppUninstalled = "app/uninstalled";
    public const string CustomersDataRequest = "customers/data_request";
    public const string CustomersRedact = "customers/redact";
    public const string ShopRedact = "shop/redact";

    public static bool IsOrders(string topic) =>
        topic is OrdersCreate or OrdersUpdated or OrdersPaid or OrdersCancelled;
}
