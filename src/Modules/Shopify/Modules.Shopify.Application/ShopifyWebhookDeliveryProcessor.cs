using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.2 y §5.5: una entrega reclamada por el worker, en este orden.
/// <list type="number">
/// <item>La tienda por su dominio. Sin ruta → <c>unknown_shop</c>, sin efecto. El webhook ya respondió 200: no revela si la tienda
/// existe (§12).</item>
/// <item><c>app/uninstalled</c> → la conexión queda <c>NeedsAttention</c> con <c>app_uninstalled</c> (adaptador de la tarea 12).</item>
/// <item>Cumplimiento: <c>customers/data_request</c> y <c>customers/redact</c> sólo se auditan (D-S9), con el dominio, el id del
/// cliente en Shopify y los ids de pedidos, nunca su email ni su teléfono; <c>shop/redact</c> borra el espejo y las entregas de la
/// tienda (no la tienda, la conexión, ni los pedidos o clientes de QEP), resuelta por su dominio y no por la ruta (Ruling S16).</item>
/// <item><c>orders/*</c> → <see cref="ShopifyOrderIngestion"/>: espejo siempre, importación si la conexión es operativa.</item>
/// </list>
/// Un payload ilegible o una cabecera que el dominio no guarda (<see cref="ShopifyPayloadException"/>) cierra la entrega con
/// <c>last_error</c>: reintentar el mismo cuerpo da lo mismo (Review Focus 2). Cualquier otra excepción sale y la entrega vuelve
/// con su lease.
/// </summary>
public sealed class ShopifyWebhookDeliveryProcessor(
    IShopifyConnectionDirectory connections,
    IShopifyConnectionHealth health,
    IShopifyOrderPayloadParser parser,
    ShopifyOrderIngestion ingestion,
    IShopifyStoreRepository stores,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyWebhookDeliveries deliveries,
    IShopifyAuditRecorder auditRecorder,
    IShopifyUnitOfWork unitOfWork,
    IClock clock)
{
    /// <summary>El ancho de <c>last_error</c> en <c>shopify.webhook_deliveries</c>.</summary>
    public const int LastErrorMaxLength = 512;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    public async Task<ShopifyDeliveryResult> ProcessAsync(ShopifyWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (string.Equals(delivery.Topic, ShopifyWebhookTopics.ShopRedact, StringComparison.Ordinal))
        {
            await RedactShopAsync(delivery, cancellationToken);
            return ShopifyDeliveryResult.Processed;
        }

        var connection = await connections.FindByShopDomainAsync(delivery.ShopDomain, cancellationToken);
        if (connection is null)
        {
            return ShopifyDeliveryResult.Ignored(ShopifyIgnoredReasons.UnknownShop);
        }

        try
        {
            switch (delivery.Topic)
            {
                case ShopifyWebhookTopics.AppUninstalled:
                    await health.ReportUninstalledAsync(connection.TenantId, connection.ConnectionId, cancellationToken);
                    return ShopifyDeliveryResult.Processed;
                case ShopifyWebhookTopics.CustomersDataRequest:
                    await AuditComplianceAsync(
                        connection, delivery, ShopifyAuditActions.ComplianceDataRequest, "orders_requested", cancellationToken);
                    return ShopifyDeliveryResult.Processed;
                case ShopifyWebhookTopics.CustomersRedact:
                    await AuditComplianceAsync(
                        connection, delivery, ShopifyAuditActions.ComplianceCustomersRedact, "orders_to_redact", cancellationToken);
                    return ShopifyDeliveryResult.Processed;
                case var topic when ShopifyWebhookTopics.IsOrders(topic):
                    await ingestion.ApplyAsync(connection, parser.ParseRest(delivery.Payload), cancellationToken);
                    return ShopifyDeliveryResult.Processed;
                default:
                    return ShopifyDeliveryResult.Ignored(ShopifyIgnoredReasons.UnsupportedTopic);
            }
        }
        catch (ShopifyPayloadException exception)
        {
            return ShopifyDeliveryResult.Failed(Describe(exception));
        }
    }

    /// <summary>
    /// Ruling S16 y §5.5: <c>shop/redact</c> borra el espejo y las entregas de la tienda resolviéndola por su dominio en
    /// <c>shopify.stores</c>, no por la ruta de Integrations: 48 horas después de desinstalar la conexión pudo borrarse y su ruta con
    /// ella. La tienda y la conexión quedan como estén. Excepción: si la ruta actual apunta a una conexión operativa, la tienda se
    /// reinstaló (Review Focus 3) y lo de esa conexión no se toca, ni las entregas, que podrían ser suyas; las tiendas viejas del
    /// mismo dominio sí se borran.
    /// </summary>
    private async Task RedactShopAsync(ShopifyWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var current = await connections.FindByShopDomainAsync(delivery.ShopDomain, cancellationToken);
        var reinstalled = current is { IsOperational: true } ? current.ConnectionId : (Guid?)null;
        foreach (var store in await stores.FindByShopDomainAsync(delivery.ShopDomain, cancellationToken))
        {
            if (store.ConnectionId != reinstalled)
            {
                await mirrors.DeleteByConnectionAsync(store.TenantId, store.ConnectionId, cancellationToken);
            }
        }

        if (reinstalled is null)
        {
            await deliveries.DeleteByShopDomainAsync(delivery.ShopDomain, delivery.Id, cancellationToken);
        }
    }

    private static string Describe(ShopifyPayloadException exception)
    {
        var text = string.IsNullOrEmpty(exception.Field)
            ? $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: {exception.Message}"
            : $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: {exception.Field}: {exception.Message}";
        return text.Length <= LastErrorMaxLength ? text : text[..LastErrorMaxLength];
    }

    private async Task AuditComplianceAsync(
        ShopifyConnectionRef connection,
        ShopifyWebhookDelivery delivery,
        string action,
        string ordersProperty,
        CancellationToken cancellationToken)
    {
        var (customerId, orderIds) = ReadCompliance(delivery.Payload, ordersProperty);
        var fields = JsonSerializer.Serialize(
            new { topic = delivery.Topic, shopDomain = delivery.ShopDomain, shopifyCustomerId = customerId, shopifyOrderIds = orderIds },
            AuditJson);
        // Sin persona detrás: el actor es la conexión, como en IConnectionHealthReporter.
        auditRecorder.Record(
            connection.TenantId,
            connection.ConnectionId,
            AuditActorType.Integration,
            action,
            ShopifyAuditActions.ConnectionResourceType,
            connection.ConnectionId.ToString(),
            fields,
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Lo único que se guarda del aviso: el id del cliente y los de sus pedidos. Lo que falte o no sea numérico se omite;
    /// la solicitud se audita igual, porque el plazo de Shopify corre aunque el cuerpo venga incompleto.</summary>
    private static (long? CustomerId, long[] OrderIds) ReadCompliance(string payload, string ordersProperty)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new ShopifyPayloadException("The Shopify compliance payload is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ShopifyPayloadException("$", "The Shopify compliance payload must be a JSON object.");
            }

            long? customerId = root.TryGetProperty("customer", out var customer)
                && customer.ValueKind == JsonValueKind.Object
                && customer.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt64(out var value)
                    ? value
                    : null;
            var orderIds = root.TryGetProperty(ordersProperty, out var orders) && orders.ValueKind == JsonValueKind.Array
                ? orders.EnumerateArray()
                    .Where(order => order.ValueKind == JsonValueKind.Number && order.TryGetInt64(out _))
                    .Select(order => order.GetInt64())
                    .ToArray()
                : [];
            return (customerId, orderIds);
        }
    }
}
