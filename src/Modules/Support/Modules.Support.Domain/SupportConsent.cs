namespace Modules.Support.Domain;

/// <summary>
/// La aceptación de los textos legales de la fase 3 al radicar sin ser anónimo (spec §9.5, plan F17): la versión aceptada, la hora, una
/// <b>foto del texto</b> y la IP. <b>Prueba legal, sin escritor</b> desde spec 2026-11-15 §5.4 (plan L10): el radicado guarda ahora
/// <see cref="SupportLegalAcceptance"/>. Las filas que ya existen se quedan; la entidad sigue mapeada en <c>SupportDbContext</c> sólo
/// para leer, porque quitarla del modelo haría que la siguiente migración de EF borrara la tabla. Por eso no tiene fábricas ni
/// mutadores; el <c>Down</c> de <c>AddSupportPublicForm</c> tampoco la borra (precedente <c>customers.data_consents</c>).
/// </summary>
public sealed class SupportConsent
{
    /// <summary>Una IPv6 en texto cabe en 45 caracteres.</summary>
    public const int VisitorIpMaxLength = 45;

    // EF Core materializa por acá: no hay otro punto de entrada.
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
}
