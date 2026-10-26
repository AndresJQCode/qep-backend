namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>La conexión de WhatsApp que el tenant eligió para enviar sus documentos (spec 2026-10-21 D5–D10). Una fila
/// de persistencia, no un agregado. Sin llaves foráneas: la conexión es de Integrations y el miembro, de Tenancy.</summary>
internal sealed class TenantWhatsAppChannelRow
{
    public Guid TenantId { get; set; }

    public Guid ConnectionId { get; set; }

    public DateTimeOffset ChosenAt { get; set; }

    public Guid ChosenByMemberId { get; set; }
}
