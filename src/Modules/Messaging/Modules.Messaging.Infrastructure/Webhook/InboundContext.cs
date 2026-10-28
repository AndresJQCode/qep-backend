namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Spec 2026-10-10 §8.1, «Antes de la transacción»: lo que la ingesta necesita y se resolvió afuera.
/// <see cref="None"/> = la conversación ya tiene cliente: no se llamó a Customers (camino común, sin costo extra).</summary>
internal sealed record InboundContext(Guid? CustomerId, bool CustomerCreated, Guid? InheritedMemberId)
{
    public static InboundContext None { get; } = new(null, false, null);
}
