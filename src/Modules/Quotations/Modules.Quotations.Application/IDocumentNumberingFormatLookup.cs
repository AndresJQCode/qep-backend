namespace Modules.Quotations.Application;

/// <summary>
/// El formato con el que este tenant numera este tipo de documento (spec 2026-09-17). Siempre
/// devuelve uno: sin fila, el default de <see cref="DocumentNumberFormat.DefaultFor"/>, que es lo
/// que el código emitía antes de que el formato fuera un dato. «Sin fila» no es un error.
///
/// El adaptador vive en Infrastructure porque la fila está en el esquema <c>quotations</c>; el
/// puerto vive acá porque quién lee el formato y cuándo es decisión del flujo.
/// </summary>
public interface IDocumentNumberingFormatLookup
{
    Task<DocumentNumberFormat> GetAsync(
        Guid tenantId,
        DocumentNumberType documentType,
        CancellationToken cancellationToken);
}
