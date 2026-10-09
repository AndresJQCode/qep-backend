namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>
/// La sección <c>Meta:App</c> (spec 2026-10-09 §9, decisión 2): una app de Meta para toda la plataforma.
/// <c>AppId</c>, <c>ConfigId</c> y <c>GraphApiVersion</c> son públicos (ConfigMap); <c>AppSecret</c> y
/// <c>WebhookVerifyToken</c> son secretos (user-secrets en local, Secret en k8s). Messaging bindea la
/// misma sección con su propia clase (P1 del plan): un módulo no referencia la infraestructura de otro.
/// <b>Vacío = ausente</b>, como <c>SecretProtectionOptions</c>.
/// </summary>
public sealed class MetaAppOptions
{
    public const string SectionName = "Meta:App";

    public const string DefaultGraphApiVersion = "v24.0";

    public string? AppId { get; set; }

    public string? ConfigId { get; set; }

    public string GraphApiVersion { get; set; } = DefaultGraphApiVersion;

    public string? AppSecret { get; set; }

    public string? WebhookVerifyToken { get; set; }

    /// <summary>D-M3: con las cinco claves el proveedor <c>whatsapp-cloud</c> sale en el catálogo y el
    /// webhook acepta tráfico; sin ellas (sólo fuera de Production) el módulo arranca igual. Método y
    /// no propiedad: <c>ConfigurationExampleTests</c> exige en el ejemplo toda propiedad con <c>get</c>.</summary>
    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(AppId)
        && !string.IsNullOrWhiteSpace(ConfigId)
        && !string.IsNullOrWhiteSpace(GraphApiVersion)
        && !string.IsNullOrWhiteSpace(AppSecret)
        && !string.IsNullOrWhiteSpace(WebhookVerifyToken);
}
