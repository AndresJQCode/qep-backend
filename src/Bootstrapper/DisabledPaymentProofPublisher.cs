using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Con <c>Quotations:PaymentProofs:PublicLinks</c> apagada (spec 2026-09-15, P1 y P3): no copia
/// nada, y los comprobantes quedan privados como hasta ahora. <see cref="UrlFor"/> devuelve null
/// aunque el comprobante tenga una copia de cuando la opción estaba encendida: apagarla deja de
/// mostrar los enlaces en el Excel, sin despublicar lo que ya se copió.
/// </summary>
internal sealed class DisabledPaymentProofPublisher : IPaymentProofPublisher
{
    public Task<string?> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) => Task.CompletedTask;

    public string? UrlFor(string publicKey) => null;
}
