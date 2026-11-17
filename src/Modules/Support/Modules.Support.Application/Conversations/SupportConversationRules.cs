using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>Lo que comparten las acciones de Soporte que parten de una conversación de Mensajería (spec §8.1 y §8.2).</summary>
internal static class SupportConversationRules
{
    /// <summary>Si el módulo está prendido, sin lanzar: la pestaña de la ficha explica por qué no puede crear en vez de responder 403. Un
    /// tenant sin fila (sólo el stub de desarrollo) cuenta como prendido, igual que <see cref="TenantModuleGuard.EnsureEnabledAsync"/>.</summary>
    public static async Task<bool> IsEnabledAsync(
        ITenantModules tenantModules, Guid tenantId, TenantModuleKey key, CancellationToken cancellationToken) =>
        await tenantModules.FindAsync(tenantId, cancellationToken) is not { } set || set.IsEnabled(key);

    /// <summary>La conversación del tenant de la ruta; otra o una que no existe es 404 <c>support.conversation.not_found</c>.</summary>
    public static async Task<SupportConversationInfo> EnsureConversationAsync(
        ISupportConversationDirectory conversations, Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        await conversations.FindAsync(tenantId, conversationId, cancellationToken)
            ?? throw SupportNotFound.Conversation(conversationId);

    /// <summary>
    /// La foto del solicitante de un ticket que nace de una conversación (plan E10). Con cliente, su contacto (regla F1 de la fase 1,
    /// <see cref="TicketRequesterNormalizer.WithCustomerContact"/>: el teléfono del cliente va al fijo, P3); sin cliente, el nombre con que se presenta el contacto
    /// (<c>profile_name</c> y, si no, el usuario de Instagram). En WhatsApp con <c>wa_id</c>, el celular es <c>+{wa_id}</c> si la foto no
    /// trae otro. Sin nada de eso, no hay foto: sin cliente, el alta responde <c>requester_required</c>.
    /// </summary>
    public static TicketRequester? RequesterFor(
        SupportConversationInfo conversation, SupportCustomerContact? customer, string tenantCountry)
    {
        var mobile = conversation.Channel == SupportConversationRef.WhatsApp
            && conversation.WaId is { Length: > 0 } waId
            && TicketRequesterNormalizer.IsMobile($"+{waId}")
                ? $"+{waId}"
                : null;
        var country = tenantCountry.Trim().ToUpperInvariant();

        if (customer is not null)
        {
            var fromCustomer = TicketRequesterNormalizer.WithCustomerContact(null, customer, country);
            if (mobile is null)
            {
                return fromCustomer;
            }

            return fromCustomer is null
                ? new TicketRequester(null, null, null, null, null, null, mobile, null, country, null)
                : fromCustomer with { MobilePhone = mobile };
        }

        var name = Fit(conversation.ProfileName) ?? Fit(conversation.Username);
        return name is null && mobile is null
            ? null
            : new TicketRequester(name, null, null, null, null, null, mobile, null, country, null);
    }

    // El nombre de la conversación puede ser más largo que el de la foto: se recorta, nunca se rechaza el alta por él.
    private static string? Fit(string? text) =>
        text?.Trim() is { Length: > 0 } trimmed
            ? trimmed.Length <= TicketRequester.NameMaxLength ? trimmed : trimmed[..TicketRequester.NameMaxLength].TrimEnd()
            : null;
}
