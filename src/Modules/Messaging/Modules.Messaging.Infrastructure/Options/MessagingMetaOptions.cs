namespace Modules.Messaging.Infrastructure.Options;

/// <summary>La parte de <c>Meta:App</c> que Messaging usa (P1 del plan): la firma del webhook, el token de
/// verificación y la versión de Graph. El validador vive en Integrations. Vacío = ausente.</summary>
public sealed class MessagingMetaOptions
{
    public const string SectionName = "Meta:App";

    public string? AppSecret { get; set; }

    public string? WebhookVerifyToken { get; set; }

    public string GraphApiVersion { get; set; } = "v24.0";
}
