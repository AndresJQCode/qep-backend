namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-11-20: una imagen de encabezado que la plantilla ya no usa pero que el historial de mensajes de QEP sigue enlazando (el enlace
/// del encabezado guardado en lo ya enviado apunta a ella; liberarla lo rompería). No se libera al cambiarla: la fila hace que Storage la siga contando como referenciada
/// hasta que se borre la plantilla, y entonces se va en cascada.
/// </summary>
public sealed class MessageTemplateRetiredHeaderImage
{
    private MessageTemplateRetiredHeaderImage()
    {
        PublicUrl = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid TemplateId { get; private set; }

    public Guid FileId { get; private set; }

    public string PublicUrl { get; private set; }

    public DateTimeOffset RetiredAt { get; private set; }

    public static MessageTemplateRetiredHeaderImage Create(Guid tenantId, Guid templateId, Guid fileId, string publicUrl, DateTimeOffset retiredAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            TemplateId = templateId,
            FileId = fileId,
            PublicUrl = publicUrl,
            RetiredAt = retiredAt,
        };
}
