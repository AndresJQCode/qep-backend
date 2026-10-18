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
}
