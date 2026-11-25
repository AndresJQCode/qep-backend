namespace Modules.Quotations.Application;

/// <summary>
/// Spec 2026-11-24 §11.1.4: antes de guardar una dirección de envío nueva en un pedido de la plataforma, si la plataforma la va a
/// poder recibir. Quotations no ve las tablas de Shopify: el adaptador vive en Bootstrapper y aplica las mismas reglas con las que
/// Shopify arma el envío, así el envío nunca falla por un dato que falta. El código postal no entra: ningún país lo exige (owner,
/// P-1, 2026-11-25).
/// </summary>
public interface IExternalOrderDeliveryCheck
{
    /// <summary>El código de <c>OrderDeliveryErrors</c> con que se rechaza, o <c>null</c> si se acepta (también un pedido que no
    /// tiene a quién empujarle la dirección).</summary>
    /// <param name="country">ISO 3166-1 alfa-2 de la parte de envío propia.</param>
    /// <param name="regionId">La región de la parte, ya validada contra su país.</param>
    Task<string?> CheckAsync(
        Guid tenantId, Guid orderId, string country, Guid? regionId, CancellationToken cancellationToken);
}
