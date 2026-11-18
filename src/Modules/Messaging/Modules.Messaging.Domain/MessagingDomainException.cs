using BuildingBlocks.Domain;

namespace Modules.Messaging.Domain;

/// <summary>Regla de negocio de Messaging: <c>ApiExceptionHandler</c> la responde 422 con su código. Desde la imagen del encabezado de
/// plantilla (spec 2026-11-17) puede marcar campos, como <c>CatalogDomainException</c>: un archivo que sólo se puede revisar leyendo Storage
/// sale con su código **y** el mapa <c>errors</c> (<c>headerImageFileId</c>), el único 422 que el formulario sabe leer para marcar el input.</summary>
public sealed class MessagingDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public MessagingDomainException(string code, string message)
        : base(code, message) =>
        FieldErrors = NoFieldErrors;

    /// <summary>Con campos marcados. Llámalo con el argumento con nombre (<c>fieldErrors:</c>): un <c>null</c> posicional sería ambiguo con
    /// la sobrecarga de la excepción interna.</summary>
    public MessagingDomainException(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    /// <summary>Cuando el 422 envuelve una falla de infraestructura (p. ej., Integrations no pudo pasar la conexión a
    /// <c>NeedsAttention</c>): la causa queda en <c>platform.request_failures</c>.</summary>
    public MessagingDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException) =>
        FieldErrors = NoFieldErrors;

    /// <summary>Con campos marcados y la causa: una violación de unicidad que el guardado traduce a un error del campo (la imagen de
    /// encabezado que ya tiene otra plantilla).</summary>
    public MessagingDomainException(string code, string message, IReadOnlyDictionary<string, string[]> fieldErrors, Exception innerException)
        : base(code, message, innerException) =>
        FieldErrors = fieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
