namespace Modules.Support.Domain;

/// <summary>
/// Una versión de los textos legales de la fase 3 (spec §9.5, plan F16): la autorización de tratamiento de datos, la política (texto o
/// URL) y los términos. <b>Prueba legal, insert-only y sin escritor</b> desde spec 2026-11-15 §5.4 (plan L10): los reemplazan los tres
/// documentos de <see cref="SupportLegalDocumentVersion"/>, y la migración <c>AddSupportLegalDocuments</c> copió la vigente de cada
/// tenant. La entidad sigue mapeada en <c>SupportDbContext</c> sólo para leer: quitarla del modelo haría que la siguiente migración de
/// EF borrara la tabla con sus filas. Por eso no tiene fábricas ni mutadores; el <c>Down</c> de <c>AddSupportPublicForm</c> tampoco la
/// borra.
/// </summary>
public sealed class SupportLegalTextVersion
{
    public const int TextMaxLength = 20_000;

    public const int PolicyUrlMaxLength = 500;

    // EF Core materializa por acá: no hay otro punto de entrada.
    private SupportLegalTextVersion()
    {
        AuthorizationText = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Única por tenant (<c>IX_legal_text_versions_tenant_version</c>).</summary>
    public int Version { get; private set; }

    public string AuthorizationText { get; private set; }

    public string? PolicyText { get; private set; }

    public string? PolicyUrl { get; private set; }

    public string? TermsText { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>La membresía que publicó la versión. Sigue en <c>SupportUserReferenceProbe</c>.</summary>
    public Guid CreatedBy { get; private set; }
}
