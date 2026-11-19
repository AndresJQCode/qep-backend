namespace Modules.Wholesale.Application;

// Spec 2026-11-18 (aviso por WhatsApp de la invitación). Mayorista no ve Customers, Integrations ni Mensajería: los tres
// puertos tienen su adaptador en Bootstrapper.

/// <summary>De un teléfono como lo escribió el mayorista a E.164 con «+», con el país del tenant como contexto; <c>null</c> si
/// no es un número válido. El adaptador usa el normalizador de Customers, así el número sale igual al que guardaría un cliente.</summary>
public interface IWholesalePhoneNormalizer
{
    string? ToE164(string phone, string country);
}

/// <summary>Una conexión de WhatsApp lista para el aviso. <c>Label</c> es su número visible, o su nombre si no tiene (D-7).</summary>
public sealed record WholesaleWhatsAppConnection(Guid Id, string Label);

/// <summary>Las conexiones <c>whatsapp-cloud</c> del mayorista que pueden mandar el aviso.</summary>
public interface IWholesaleWhatsAppConnections
{
    /// <summary>Las activas con la plantilla del aviso aprobada; vacía = no hay cómo avisar.</summary>
    Task<IReadOnlyList<WholesaleWhatsAppConnection>> ListReadyAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>El label de una conexión activa, o <c>null</c> si ya no existe o no se puede usar.</summary>
    Task<string?> FindLabelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>
/// Lo que el procesador del aviso le pasa al adaptador de envío. <c>MessageId</c> es el id del mensaje del outbox y se usa
/// como <c>clientId</c> (D-5): un reintento no manda dos avisos. <c>Link</c> (spec 2026-11-19 §2) es el link de la invitación
/// armado con el token del evento nuevo, o <c>null</c> para un aviso viejo sin link. El adaptador decide si lo manda: Mensajería
/// guarda el texto de la plantilla tal cual, así que hoy no lo hace (ver <c>WholesaleInvitationNoticeSender</c>).
/// </summary>
public sealed record WholesaleInvitationNotice(
    Guid MessageId,
    Guid TenantId,
    Guid ConnectionId,
    Guid SentByMemberId,
    string Phone,
    string ContactName,
    string Email,
    string? Link = null)
{
    // El link lleva el token y el teléfono es dato personal: un ToString() que llegue a un log sólo muestra ids.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(
            System.Globalization.CultureInfo.InvariantCulture,
            $"MessageId = {MessageId}, TenantId = {TenantId}, ConnectionId = {ConnectionId}");
        return true;
    }
}

/// <summary>El resultado del envío. <c>Failed.Code</c> es el código que se guarda en la invitación:
/// <c>template_not_approved</c>, <c>connection_unavailable:&lt;motivo&gt;</c> o <c>send_failed:&lt;código&gt;</c>.</summary>
public abstract record WholesaleNoticeSendResult
{
    public sealed record Sent : WholesaleNoticeSendResult;

    public sealed record Failed(string Code) : WholesaleNoticeSendResult;
}

public interface IWholesaleInvitationNoticeSender
{
    Task<WholesaleNoticeSendResult> SendAsync(WholesaleInvitationNotice notice, CancellationToken cancellationToken);
}
