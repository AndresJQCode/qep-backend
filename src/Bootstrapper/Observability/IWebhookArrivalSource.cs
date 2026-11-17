namespace Bootstrapper.Observability;

/// <summary>Spec 2026-11-16: cuándo llegó el último webhook de Meta de cada proveedor.</summary>
public interface IWebhookArrivalSource
{
    /// <summary>Clave: el valor de <c>messaging.webhook_deliveries.provider</c> (<c>whatsapp</c>, <c>instagram</c>); valor: el
    /// <c>received_at</c> de la entrega más reciente. Un proveedor sin entregas no aparece.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadLastReceivedAsync(CancellationToken cancellationToken);
}
