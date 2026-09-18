using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Lee la fila de <c>quotations.document_numbering_formats</c>, o devuelve el default si no hay.
/// Una consulta por (tenant, tipo) y por request, sin caché entre requests: la tabla se escribe a
/// mano con el runbook, y un formato nuevo tiene que verse en el documento siguiente, no en el
/// despliegue siguiente.
///
/// Los rangos se revalidan en <see cref="DocumentNumberFormat.Create"/> aunque la base los tenga
/// como CHECK: una base restaurada o migrada a mano puede traer una fila que el CHECK nunca vio, y
/// entonces la emisión falla con código de dominio y 422 en vez de emitir un número a medias.
/// </summary>
internal sealed class DocumentNumberingFormatLookup(QuotationsDbContext dbContext)
    : IDocumentNumberingFormatLookup
{
    public async Task<DocumentNumberFormat> GetAsync(
        Guid tenantId,
        DocumentNumberType documentType,
        CancellationToken cancellationToken)
    {
        var columnValue = ColumnValueOf(documentType);
        var row = await dbContext.DocumentNumberingFormats
            .AsNoTracking()
            .SingleOrDefaultAsync(
                format => format.TenantId == tenantId && format.DocumentType == columnValue,
                cancellationToken);

        return row is null
            ? DocumentNumberFormat.DefaultFor(documentType)
            : DocumentNumberFormat.Create(row.Prefix, row.IncludeYear, row.YearSeparator, row.MinDigits);
    }

    private static string ColumnValueOf(DocumentNumberType documentType) => documentType switch
    {
        DocumentNumberType.Quotation => "quotation",
        DocumentNumberType.Order => "order",
        _ => throw new ArgumentOutOfRangeException(nameof(documentType)),
    };
}
