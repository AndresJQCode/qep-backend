using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.8: de una falla de Graph al código de dominio. Discrimina por <c>error_user_title</c> y <c>error_user_msg</c>,
/// nunca sólo por <c>code = 100</c> (y no por <c>error_subcode</c>, que Meta ya no devuelve): mismo criterio que el nombre de índice
/// en los <c>23505</c>. Vive en Application porque traduce un <see cref="MessagingGraphFailure"/> ya parseado y porque quien la llama
/// tiene que reaccionar al token vencido en el mismo lugar (<see cref="FailAsync"/>).
/// <para>
/// <see cref="Translate"/> es para las operaciones que escriben en Meta (crear, borrar). El listado del refresco no lo usa: ahí
/// cualquier falla que no sea el token es <c>messaging.templates.unavailable</c> (<c>TemplateMirrorSync</c>).
/// </para>
/// </summary>
public static class TemplateGraphErrors
{
    /// <summary>§4.8 «token vencido o permiso faltante»: HTTP 401 o 403, <c>code = 190</c> (token) y los códigos de permiso de Graph
    /// (<c>10</c> y <c>200</c>–<c>299</c>). El <c>HttpStatus = 0</c> de una falla de red no es un status y no cuenta.</summary>
    public static bool IsTokenRejected(MessagingGraphFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.HttpStatus is 401 or 403
            || failure.Code is MetaErrorCodes.TokenExpired or 10 or >= 200 and <= 299;
    }

    public static MessagingDomainException Translate(MessagingGraphFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.HttpStatus == 0 || failure.HttpStatus >= 500)
        {
            return new MessagingDomainException(MessagingErrorCodes.TemplatesUnavailable, "WhatsApp did not answer the template operation.");
        }

        if (IsTokenRejected(failure))
        {
            return new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "WhatsApp rejected the connection credentials.");
        }

        var title = failure.UserTitle ?? string.Empty;
        var message = failure.UserMessage ?? string.Empty;
        if (Mentions(title, "already exist") || Mentions(message, "already exist") || InDeletionQuarantine(title) || InDeletionQuarantine(message))
        {
            return new MessagingDomainException(MessagingErrorCodes.TemplateNameTaken, "Meta already has a template with that name in that language.");
        }

        if (Mentions(title, "limit"))
        {
            return new MessagingDomainException(MessagingErrorCodes.TemplateLimitReached, "The WhatsApp Business account reached its template limit.");
        }

        // El único message en español del módulo: la pantalla lo muestra tal cual, porque no hay diccionario para un rechazo arbitrario.
        var detail = message.Length > 0 ? message : title.Length > 0 ? title : failure.Reason;
        return new MessagingDomainException(MessagingErrorCodes.TemplateRejectedByMeta, $"WhatsApp no aceptó la plantilla: {detail}");
    }

    /// <summary>Lo que hace todo handler ante una falla de Graph en una operación de plantillas: si fue el token o un permiso, reporta la
    /// conexión (igual que los envíos, con <c>token_expired</c>) y devuelve <c>connection_unavailable</c>; si no, la traducción.</summary>
    public static async Task<MessagingDomainException> FailAsync(
        IMessagingConnectionDirectory connections, Guid tenantId, Guid connectionId, MessagingGraphFailure failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (IsTokenRejected(failure))
        {
            await connections.ReportRejectedAsync(tenantId, connectionId, MetaErrorCodes.RejectedReason(MetaErrorCodes.TokenExpired), cancellationToken);
        }

        return Translate(failure);
    }

    /// <summary>Revisión final F3: Meta bloquea un tiempo el nombre (o ese idioma) de una plantilla borrada. El texto exacto del error no está
    /// confirmado (handoff de Meta, «a confirmar»), así que la regla es explícita: «being deleted», o «deleted» junto a «language» o
    /// «template». Tratarlo como <c>name_taken</c> es lo que hace que el aprovisionamiento salte a la versión siguiente en vez de recalcular
    /// la misma después de que el admin borró la <c>Quotation</c> desde la pantalla.</summary>
    private static bool InDeletionQuarantine(string text) =>
        Mentions(text, "being deleted") || (Mentions(text, "deleted") && (Mentions(text, "language") || Mentions(text, "template")));

    private static bool Mentions(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
