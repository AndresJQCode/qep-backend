using System.Text.RegularExpressions;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18: el adaptador de <see cref="IWholesaleInvitationNoticeSender"/> hacia Mensajería, como
/// <see cref="ServiceContractsMetaWhatsAppSender"/> para los cobros. Llena las tres variables propias de la plantilla
/// (<c>nombre_contacto</c>, <c>mayorista</c> y <c>correo</c>; las fijas las pone Mensajería) y traduce el resultado a los
/// códigos que se guardan en la invitación. El nombre del mayorista se lee al entregar, como el correo: un tenant renombrado
/// sale con el nombre vigente.
/// <para>Spec 2026-11-19 §2: desde el evento con link, <see cref="WholesaleInvitationNotice.Link"/> llega hasta acá, pero
/// <b>no se le pasa a Mensajería</b>. <c>PlainTemplateOutbound</c> guarda en <c>messaging.messages</c> el texto armado de la
/// plantilla (y sus valores en el detalle) tal cual, sin forma de enmascarar un valor, y el token plano no puede guardarse. Hasta
/// que Mensajería sepa enmascararlo y exista la plantilla con <c>{{link}}</c> (ítem aparte), el aviso sale como antes, sin
/// link.</para>
/// </summary>
internal sealed partial class WholesaleInvitationNoticeSender(
    IWholesaleInvitationNoticeOutbound outbound, IWholesaleInvitationNoticeReadiness readiness, ITenantDirectory tenants)
    : IWholesaleInvitationNoticeSender
{
    // Lo que Mensajería responde a una variable que Meta no aceptaría; acá no se llega a llamar.
    private const string RejectedCode = "send_failed:messaging.message.rejected";

    public async Task<WholesaleNoticeSendResult> SendAsync(WholesaleInvitationNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);

        // La vigente puede ser `_v2`: el nombre sale de la fila, no de la constante.
        if (await readiness.FindApprovedAsync(notice.TenantId, notice.ConnectionId, cancellationToken) is not { } template)
        {
            return new WholesaleNoticeSendResult.Failed("template_not_approved");
        }

        // Meta rechaza una variable vacía.
        var wholesaler = OneLine(await tenants.GetDisplayNameAsync(new TenantId(notice.TenantId), cancellationToken));
        var contact = OneLine(notice.ContactName);
        if (wholesaler.Length == 0 || contact.Length == 0)
        {
            return new WholesaleNoticeSendResult.Failed(RejectedCode);
        }

        var result = await outbound.SendAsync(
            new WholesaleInvitationNoticeSend(
                notice.TenantId,
                notice.ConnectionId,
                notice.SentByMemberId,
                ClientId: notice.MessageId,
                WaId: WaIdOf(notice.Phone),
                RecipientName: contact,
                template,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SystemTemplateVariables.ContactName] = contact,
                    [WholesaleInvitationNoticeTemplateDefinition.WholesalerKey] = wholesaler,
                    [WholesaleInvitationNoticeTemplateDefinition.EmailKey] = notice.Email,
                }),
            cancellationToken);

        return result switch
        {
            WholesaleInvitationNoticeSendResult.Sent => new WholesaleNoticeSendResult.Sent(),
            WholesaleInvitationNoticeSendResult.ConnectionUnavailable unavailable =>
                new WholesaleNoticeSendResult.Failed($"connection_unavailable:{unavailable.Reason ?? "unknown"}"),
            WholesaleInvitationNoticeSendResult.Failed failed => new WholesaleNoticeSendResult.Failed($"send_failed:{failed.Code}"),
            _ => new WholesaleNoticeSendResult.Failed("send_failed:unknown"),
        };
    }

    // El `wa_id` de Meta son sólo dígitos con indicativo; el teléfono guardado es E.164 («+573001234567»).
    private static string WaIdOf(string phoneE164) => new(phoneE164.Where(char.IsAsciiDigit).ToArray());

    // Meta rechaza saltos de línea, tabulaciones y más de 4 espacios seguidos: una línea, espacios simples.
    private static string OneLine(string? value) => value is null ? string.Empty : Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
