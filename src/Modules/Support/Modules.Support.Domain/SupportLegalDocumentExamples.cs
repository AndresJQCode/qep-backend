namespace Modules.Support.Domain;

/// <summary>
/// QEP's sample text of each legal document (spec 2026-11-15 §5.1, D-4, plan L9): version 0, shown to the console marked as an example
/// until the tenant publishes its own, never stored and never accepted. A starting point, not legal advice: the tenant answers for the
/// content, and the lawyer reviews these texts before production. They are already in the sanitizer's canonical form
/// (<c>EachKindHasAnExampleAtVersionZero</c>).
/// </summary>
public static class SupportLegalDocumentExamples
{
    private const string PrivacyNotice =
        "<p>Al enviar este formulario autorizas de manera previa, expresa e informada a la empresa para recolectar, almacenar, usar y "
        + "tratar tus datos personales con el fin de radicar, gestionar y responder tu petición, queja, reclamo, sugerencia o "
        + "felicitación, y de contactarte sobre ella. Puedes conocer, actualizar, rectificar y suprimir tus datos, y revocar esta "
        + "autorización, en los términos de la Ley 1581 de 2012 y su política de tratamiento de datos.</p>"
        + "<p>Tratamos tus datos personales sólo para gestionar tu solicitud y cumplir obligaciones legales. Los conservamos mientras "
        + "dure el trámite y el término que exija la ley. Para ejercer tus derechos como titular, escríbenos por los canales de "
        + "contacto de la empresa.</p>";

    private const string AnonymousNotice =
        "<p>Puedes radicar tu solicitud sin darnos tus datos personales: no te pediremos nombre, documento ni datos de contacto.</p>"
        + "<p>Como no tenemos cómo contactarte, la respuesta se publica en la consulta de tu radicado y sólo la ves con el PIN que te "
        + "damos al final. Guárdalo: si lo pierdes, no se puede recuperar ni reenviar.</p>";

    private const string Terms =
        "<p>Este sistema recibe peticiones, quejas, reclamos, sugerencias, felicitaciones y solicitudes de soporte. Al radicar recibes "
        + "un número de radicado y un PIN para consultar el estado de tu solicitud.</p>"
        + "<p>Respondemos dentro de los plazos que fija la ley para cada tipo de solicitud. Si la información que nos das no alcanza "
        + "para tramitarla, podremos pedirte más datos.</p>"
        + "<p>Usa este canal con respeto y con información veraz. Las solicitudes ofensivas o sin relación con nuestros productos o "
        + "servicios pueden no tramitarse.</p>";

    public static SupportLegalDocumentVersion For(SupportLegalDocumentKind kind, Guid tenantId) =>
        SupportLegalDocumentVersion.Example(tenantId, kind, kind switch
        {
            SupportLegalDocumentKind.PrivacyNotice => PrivacyNotice,
            SupportLegalDocumentKind.AnonymousNotice => AnonymousNotice,
            SupportLegalDocumentKind.Terms => Terms,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown legal document kind."),
        });
}
