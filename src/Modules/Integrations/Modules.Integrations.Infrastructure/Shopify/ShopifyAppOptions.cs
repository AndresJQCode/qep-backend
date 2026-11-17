namespace Modules.Integrations.Infrastructure.Shopify;

/// <summary>
/// La sección <c>Shopify:App</c> (spec 2026-10-15 §4.2): una app de Shopify para toda la plataforma. <c>ClientId</c>,
/// la versión, los scopes, los orígenes de retorno y la URL del webhook son públicos (ConfigMap); <c>ClientSecret</c> es
/// secreto (user-secrets en local, Secret de k8s en producción, nunca el ConfigMap). <b>Vacío = ausente</b>, como
/// <c>MetaInstagramOptions</c>.
/// </summary>
public sealed class ShopifyAppOptions
{
    public const string SectionName = "Shopify:App";

    /// <summary>D-S29: la estable del trimestre en que se implementó (2026-10-18).</summary>
    public const string DefaultApiVersion = "2026-10";

    /// <summary><c>write_orders</c> desde el spec 2026-11-14: la corrección del asesor viaja a Shopify con <c>orderUpdate</c>. Según
    /// el owner, las tiendas ya enlazadas lo tienen concedido (V-13, por verificar con <c>currentAppInstallation.accessScopes</c>);
    /// sumarlo acá es para que una instalación nueva lo pida. Un canje nuevo sin él falla con <c>scopes_missing</c>.
    /// <para><c>read_products</c> desde el spec 2026-11-17 §5.1: la importación de productos y los <c>products/*</c>. Una tienda
    /// instalada antes no lo tiene: importar le responde 422 <c>shopify.products.scope_missing</c> con la URL para reautorizar, y el
    /// registro de webhooks no le pide los <c>products/*</c> hasta que el canje guarde el scope (P10).</para></summary>
    public const string DefaultScopes = "read_orders,write_orders,read_customers,read_products";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string ApiVersion { get; set; } = DefaultApiVersion;

    /// <summary>Separados por coma, como los devuelve Shopify en el canje.</summary>
    public string Scopes { get; set; } = DefaultScopes;

    /// <summary>Los orígenes (esquema, host y puerto) desde los que el SPA puede volver con el <c>code</c>. En local
    /// <c>http://localhost:3002</c>; en producción el del SPA, desde el ConfigMap.</summary>
    public List<string> AllowedRedirectOrigins { get; set; } = [];

    /// <summary>D-S17: configuración global, no se deduce del request. En local, la del túnel si lo hay.</summary>
    public string? WebhookCallbackUrl { get; set; }

    /// <summary>D-M3 aplicado a Shopify: con el par de la app el proveedor sale en el catálogo; sin él (sólo fuera de
    /// Production) el módulo arranca igual. Método y no propiedad: <c>ConfigurationExampleTests</c> exige en el
    /// ejemplo toda propiedad con <c>get</c>.</summary>
    public bool IsConfigured() => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
