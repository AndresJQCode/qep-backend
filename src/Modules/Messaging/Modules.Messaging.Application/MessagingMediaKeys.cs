namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §8.6 y spec 2026-10-11 §6.3: dónde vive en R2 (bucket privado) el medio de un mensaje,
/// entrante o saliente. Vive en Application porque el envío de una nota de voz la necesita y no ve Infrastructure.</summary>
public static class MessagingMediaKeys
{
    public static string For(Guid tenantId, Guid messageId) => $"messaging/{tenantId}/{messageId}";
}
