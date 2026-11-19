using FluentValidation;
using FluentValidation.Results;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Spec 2026-11-18: las dos reglas del aviso por WhatsApp que repiten invitar y editar. El teléfono se guarda en E.164 con el
/// país del tenant; la conexión sale de las listas para el aviso, según el contrato.
/// </summary>
internal static class DistributorWhatsAppNotice
{
    /// <summary>El teléfono en E.164, o <c>null</c> si viene vacío o sólo con espacios. Uno que no normaliza es un 422 en
    /// <c>Phone</c>.</summary>
    public static async Task<string?> NormalizeAsync(
        ITenantDirectory tenants,
        IWholesalePhoneNormalizer phones,
        Guid tenantId,
        string? phone,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var settings = await tenants.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        var normalized = phones.ToE164(phone.Trim(), settings?.Country ?? DistributorDefaults.Country);
        if (normalized is not { Length: > 0 and <= Domain.Distributor.PhoneMaxLength })
        {
            throw new ValidationException([new ValidationFailure("Phone", "The phone is not valid.")]);
        }

        return normalized;
    }

    /// <summary>
    /// La conexión del aviso. Pedida: tiene que estar lista, o es <c>whatsapp_unavailable</c>. Sin pedir: la única lista; con
    /// varias, <c>whatsapp_connection_required</c>; sin ninguna, <c>whatsapp_unavailable</c>.
    /// </summary>
    public static async Task<Guid> ResolveConnectionAsync(
        IWholesaleWhatsAppConnections connections,
        Guid tenantId,
        Guid? requested,
        CancellationToken cancellationToken)
    {
        var ready = await connections.ListReadyAsync(tenantId, cancellationToken);
        if (requested is { } chosen)
        {
            return ready.Any(connection => connection.Id == chosen) ? chosen : throw WholesaleConflicts.WhatsAppUnavailable();
        }

        return ready.Count switch
        {
            1 => ready[0].Id,
            0 => throw WholesaleConflicts.WhatsAppUnavailable(),
            _ => throw WholesaleConflicts.WhatsAppConnectionRequired(),
        };
    }

    /// <summary>
    /// Escribe el evento del aviso con link (spec 2026-11-19 §2) con la generación vigente; se llama después de mutar la
    /// invitación, con el <paramref name="token"/> plano que sale también en el evento del correo (el hash ya está guardado).
    /// Autor (D-6): la membresía activa de quien invita o reenvía y, si no tiene (stub de desarrollo), el asesor, que ya se
    /// validó activo.
    /// </summary>
    public static async Task QueueAsync(
        IWholesaleEventPublisher events,
        IMembershipDirectory memberships,
        Guid actorUserId,
        Domain.Distributor distributor,
        string token,
        string correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (distributor.Phone is not { } phone || distributor.WhatsAppConnectionId is not { } connectionId)
        {
            throw new InvalidOperationException("A notice needs the distributor's phone and WhatsApp connection.");
        }

        // El link del aviso tiene que abrir la invitación vigente: un token de otra generación sería un link muerto.
        if (!string.Equals(InvitationTokens.HashOf(token), distributor.Invitation.TokenHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The notice must carry the token of the current invitation.");
        }

        var sender = await memberships.FindActiveMembershipIdAsync(actorUserId, distributor.TenantId, cancellationToken)
            ?? distributor.AdvisorMemberId;
        events.InvitationLinkRequested(
            distributor.TenantId,
            distributor.Id.Value,
            phone,
            connectionId,
            sender,
            distributor.ContactName,
            distributor.Email,
            token,
            distributor.Invitation.Generation,
            correlationId,
            now);
    }

    /// <summary>D-8: <c>audit.entries</c> no tiene columna de datos; el flag va como <c>clave=valor</c> en los campos.</summary>
    public static string AuditFlag(bool noticeQueued) => noticeQueued ? "whatsAppNotice=true" : "whatsAppNotice=false";
}
