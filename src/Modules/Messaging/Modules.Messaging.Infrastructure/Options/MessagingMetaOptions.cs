namespace Modules.Messaging.Infrastructure.Options;

/// <summary>La parte de <c>Meta:App</c> que Messaging usa (P1 del plan): la firma del webhook, el token de
/// verificación, el <c>AppId</c> de la subida del PDF de ejemplo y la versión de Graph. El validador vive en Integrations. Vacío = ausente.</summary>
public sealed class MessagingMetaOptions
{
    public const string SectionName = "Meta:App";

    public string? AppSecret { get; set; }

    public string? WebhookVerifyToken { get; set; }

    /// <summary>Spec 2026-10-14 §4.5: la Resumable Upload va a <c>/{AppId}/uploads</c>. Es el mismo valor público que Integrations usa
    /// para el signup; Messaging lo bindea aparte porque no referencia su infraestructura.</summary>
    public string? AppId { get; set; }

    public string GraphApiVersion { get; set; } = "v24.0";
}
