namespace Modules.Integrations.Application.Shopify;

/// <summary>
/// La app de Shopify de toda la plataforma (spec 2026-10-15 §4.2). <see cref="IsConfigured"/> es D-M3 aplicado a
/// Shopify: sin <c>ClientId</c> y <c>ClientSecret</c> (sólo fuera de Production, donde <c>ValidateOnStart</c> no deja
/// arrancar sin ellos) el proveedor <c>shopify</c> no sale en el catálogo. <see cref="ClientSecret"/> está acá porque
/// el canje del <c>code</c> y la firma de los webhooks lo necesitan: nunca va a un DTO, un log ni la auditoría.
/// </summary>
public interface IShopifyAppSettings
{
    bool IsConfigured { get; }

    string? ClientId { get; }

    string? ClientSecret { get; }

    /// <summary>La versión de la Admin API (<c>2026-10</c> por defecto, D-S29).</summary>
    string ApiVersion { get; }

    /// <summary>Lo que se pide en la autorización; el enlace exige que lo concedido lo contenga.</summary>
    IReadOnlyList<string> Scopes { get; }

    /// <summary>La URL pública que QEP registra en cada tienda (D-S17); <c>null</c> en local sin túnel.</summary>
    Uri? WebhookCallbackUrl { get; }

    /// <summary>Compara el origen (esquema, host y puerto) del <c>redirect_uri</c> con la lista blanca, no la URL completa.</summary>
    bool IsRedirectAllowed(Uri redirectUri);
}
