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
    /// <summary>D-S31: «Marcar como revisado» sobre una fila sin avisos.</summary>
    public const string OrderNoWarnings = "shopify.order.no_warnings";

    /// <summary>§5.1: firma <c>X-Shopify-Hmac-Sha256</c> ausente o inválida (401, sin encolar). No está en la tabla de §7.1: es el
    /// análogo de <c>messaging.webhook.signature_invalid</c>, a ratificar.</summary>
    public const string WebhookSignatureInvalid = "shopify.webhook.signature_invalid";
}
