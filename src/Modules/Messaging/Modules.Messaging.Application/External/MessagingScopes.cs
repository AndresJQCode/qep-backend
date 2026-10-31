namespace Modules.Messaging.Application;

/// <summary>Los scopes de API que Messaging ofrece a las claves de integración (ApiAccess no los conoce: se registran en el composition root).</summary>
public static class MessagingScopes
{
    /// <summary>Enviar plantillas aprobadas de WhatsApp. Incluye consultar las plantillas disponibles.</summary>
    public const string TemplatesSend = "whatsapp.templates.send";
}
