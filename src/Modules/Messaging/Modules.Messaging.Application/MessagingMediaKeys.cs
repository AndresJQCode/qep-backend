namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §8.6 y spec 2026-10-11 §6.3: dónde vive en R2 (bucket privado) el medio de un mensaje,
/// entrante o saliente. Vive en Application porque el envío de una nota de voz la necesita y no ve Infrastructure.</summary>
public static class MessagingMediaKeys
{
    public static string For(Guid tenantId, Guid messageId) => $"messaging/{tenantId}/{messageId}";

    /// <summary>Spec 2026-11-09 D6: dónde espera el adjunto de un mensaje programado hasta que sale (hasta 24 h). Las imágenes van
    /// siempre con <c>.jpg</c>, porque lo que se guarda es la comprimida. Sin regla de lifecycle en este prefijo.</summary>
    public static string Scheduled(Guid tenantId, Guid scheduledMessageId, string extension)
    {
        var normalized = extension.Trim().ToLowerInvariant();
        if (normalized.Length > 0 && normalized[0] != '.')
        {
            normalized = "." + normalized;
        }

        return $"messaging/scheduled/{tenantId}/{scheduledMessageId}{normalized}";
    }

    /// <summary>Dónde deja el front el adjunto del asesor antes de confirmarlo. La clave lleva tenant, conversación y miembro
    /// que salen del contexto autenticado: un <c>uploadId</c> ajeno no resuelve a ningún objeto. La extensión se normaliza
    /// (minúsculas, con punto) porque el sniffer decide por ella.</summary>
    public static string Staging(Guid tenantId, Guid conversationId, Guid memberId, Guid uploadId, string extension)
    {
        var normalized = extension.Trim().ToLowerInvariant();
        if (normalized.Length > 0 && normalized[0] != '.')
        {
            normalized = "." + normalized;
        }

        return $"messaging/staging/{tenantId}/{conversationId}/{memberId}/{uploadId}{normalized}";
    }
}
