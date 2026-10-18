using BuildingBlocks.Domain;

namespace Modules.Shopify.Domain;

/// <summary>Regla de negocio del módulo Shopify: <c>ApiExceptionHandler</c> la responde 422 con su código.</summary>
public sealed class ShopifyDomainException : DomainException
{
    public ShopifyDomainException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>Cuando el 422 envuelve una falla de la base traducida en <c>ShopifyUnitOfWork</c>: la causa queda en
    /// <c>platform.request_failures</c>.</summary>
    public ShopifyDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException)
    {
    }
}

/// <summary>Spec 2026-10-15 §7.1: los códigos del módulo, en un solo lugar.</summary>
public static class ShopifyErrorCodes
{
    /// <summary>§5.3 y §7.1: ya hay una corrida de la conciliación de esa tienda (lease vigente). 409.</summary>
    public const string StoreSyncInProgress = "shopify.store.sync_in_progress";

    /// <summary>§5.3: la conexión no está operativa (pausada, <c>NeedsAttention</c>, sin token) o Shopify acaba de rechazar su
    /// credencial (401/402) y quedó reportada. 422. No está en la tabla de §7.1: a ratificar (tarea 15).</summary>
    public const string StoreNotOperational = "shopify.store.not_operational";

    /// <summary>§5.3: Shopify no respondió (red, 5xx) o siguió en <c>THROTTLED</c> tres veces seguidas; la ventana no se movió y la
    /// próxima corrida lo vuelve a intentar. 503. No está en la tabla de §7.1: a ratificar (tarea 15).</summary>
    public const string StoreSyncUnavailable = "shopify.store.sync_unavailable";

    /// <summary>§6.1 y §7.1: el asesor de la tienda no es una membresía activa del tenant. 422.</summary>
    public const string StoreAdvisorNotMember = "shopify.store.advisor_not_member";

    /// <summary>§6.1 y §7.1: la cuenta de cobro no existe en esa empresa del tenant (banco, número y moneda). 422.</summary>
    public const string StoreBillingAccountNotFound = "shopify.store.billing_account_not_found";

    /// <summary>§7.1: la clasificación por defecto no es del tenant. 422.</summary>
    public const string StoreClassificationNotFound = "shopify.store.classification_not_found";

    /// <summary>§7.1: la fila del espejo no existe en el tenant. 404 (el tenant de la ruta ya se validó contra el activo).</summary>
    public const string OrderNotFound = "shopify.order.not_found";

    /// <summary>§6.4 y §7.1: reintentar una fila que no está <c>Waiting</c>. 422.</summary>
    public const string OrderNotWaiting = "shopify.order.not_waiting";

    /// <summary>D-S31: «Marcar como revisado» sobre una fila sin avisos.</summary>
    public const string OrderNoWarnings = "shopify.order.no_warnings";

    /// <summary>§5.1: firma <c>X-Shopify-Hmac-Sha256</c> ausente o inválida (401, sin encolar). No está en la tabla de §7.1: es el
    /// análogo de <c>messaging.webhook.signature_invalid</c>, a ratificar.</summary>
    public const string WebhookSignatureInvalid = "shopify.webhook.signature_invalid";
}
