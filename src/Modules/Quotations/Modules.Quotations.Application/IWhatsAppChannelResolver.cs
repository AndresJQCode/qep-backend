using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>El canal efectivo de un envío. <see cref="Sender"/> es nulo si y sólo si
/// <see cref="Mode"/> es <see cref="WhatsAppMode.Disabled"/>.</summary>
public sealed record WhatsAppChannel(WhatsAppMode Mode, IWhatsAppSender? Sender);

/// <summary>
/// Resuelve por dónde sale el WhatsApp de un tenant, al principio de cada envío (spec 2026-10-07,
/// «Reglas de resolución»): una lectura por PK, sin caché. Una caché obligaría a invalidar en el
/// PUT y entre réplicas, y el envío ya paga qcode-pdf, R2 y Zenvia.
/// </summary>
public interface IWhatsAppChannelResolver
{
    Task<WhatsAppChannel> ResolveAsync(Guid tenantId, CancellationToken cancellationToken);
}
