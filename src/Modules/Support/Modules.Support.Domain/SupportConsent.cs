namespace Modules.Support.Domain;

/// <summary>
/// La aceptación de los textos legales al radicar sin ser anónimo (spec §9.5, plan F17): la versión aceptada, la hora, una
/// <b>foto del texto</b> y la IP. <b>Prueba legal, insert-only</b>: sin mutadores, una fila por ticket, en la misma transacción que
/// el ticket. La tabla no tiene FK (ni al ticket ni a la versión) y el <c>Down</c> de <c>AddSupportPublicForm</c> no la borra
/// (precedente <c>customers.data_consents</c>). Lo que prueba es el texto que la persona vio, no el que hay hoy. Un ticket anónimo no
/// deja fila.
/// </summary>
public sealed class SupportConsent
{
    /// <summary>Una IPv6 en texto cabe en 45 caracteres.</summary>
    public const int VisitorIpMaxLength = 45;

    // EF Core materializa por acá; Record es el único punto de entrada de código.
    private SupportConsent()
    {
        AuthorizationText = string.Empty;
        VisitorIp = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportTicketId TicketId { get; private set; }

    public Guid LegalTextVersionId { get; private set; }

    public int LegalTextVersion { get; private set; }

    public DateTimeOffset AcceptedAt { get; private set; }

    public string AuthorizationText { get; private set; }

    public string? PolicyText { get; private set; }

    public string? PolicyUrl { get; private set; }

    public string? TermsText { get; private set; }

    public string VisitorIp { get; private set; }

    /// <summary>El texto tiene que ser una versión publicada del mismo tenant: el ejemplo no se acepta.</summary>
    public static SupportConsent Record(
        Guid id, Guid tenantId, SupportTicketId ticketId, SupportLegalTextVersion text, string visitorIp, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A consent needs its id.", nameof(id));
        }

        if (text.IsExample || text.Version < 1 || text.TenantId != tenantId)
        {
            throw new ArgumentException("A consent needs a published legal text version of the same tenant.", nameof(text));
        }

        var ip = visitorIp?.Trim();
        if (string.IsNullOrEmpty(ip) || ip.Length > VisitorIpMaxLength)
        {
            throw new ArgumentException(
                $"The visitor IP must have between 1 and {VisitorIpMaxLength} characters.", nameof(visitorIp));
        }

        return new SupportConsent
        {
            Id = id,
            TenantId = tenantId,
            TicketId = ticketId,
            LegalTextVersionId = text.Id,
            LegalTextVersion = text.Version,
            AcceptedAt = now,
            AuthorizationText = text.AuthorizationText,
            PolicyText = text.PolicyText,
            PolicyUrl = text.PolicyUrl,
            TermsText = text.TermsText,
            VisitorIp = ip,
        };
    }
}
