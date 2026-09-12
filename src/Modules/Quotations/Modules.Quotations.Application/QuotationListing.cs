using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Lo que el listado y su Excel comparten fuera de la consulta: como se interpretan los filtros
/// que llegan como texto y como se completa cada fila. Vive en un solo lugar para que la tabla y
/// el archivo no puedan empezar a contar cosas distintas; la mitad SQL la comparte
/// <c>QuotationRepository</c>.
/// </summary>
internal static class QuotationListing
{
    // El valor llega como texto libre por query string, así que una entrada que no matchea
    // ningún valor del enum es un 422 con código de dominio -- no un filtro que en silencio no
    // devuelve nada, ni un 500 de un cast que falla.
    public static QuotationStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return Enum.TryParse<QuotationStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "quotation.quotation.status_invalid",
                $"'{status}' is not a valid quotation status.");
    }

    // El NIT no vive en Quotation: se resuelve a ids contra Customers antes de filtrar, mismo
    // criterio que ListCustomersHandler con el filtro de Departamento -> ids de ciudad. Sin
    // termino devuelve null ("sin filtro"); con termino sin match, una coleccion vacia, y la
    // busqueda ya sabe que no hay nada que traer.
    public static async Task<IReadOnlyCollection<Guid>?> ResolveClientIdsByNitAsync(
        IQuotationCustomerLookup customerLookup,
        Guid tenantId,
        string? clientNit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientNit))
        {
            return null;
        }

        var matchedIds = await customerLookup.SearchIdsByIdentificationAsync(
            tenantId, clientNit, cancellationToken);
        return matchedIds.ToArray();
    }

    public static async Task<IReadOnlyList<QuotationListItemDto>> ToListItemsAsync(
        IQuotationCustomerLookup customerLookup,
        IQuotationAdvisorLookup advisorLookup,
        Guid tenantId,
        IReadOnlyList<Quotation> quotations,
        CancellationToken cancellationToken)
    {
        // Los nombres de cliente de todas las filas en una sola consulta: la tabla muestra el
        // nombre, no el id, y resolverlo del lado del que consume el listado es un GET por fila
        // contra Customers. Los ids van sin repetir -- varias cotizaciones del mismo cliente son
        // lo normal.
        var clientNames = quotations.Count == 0
            ? new Dictionary<Guid, string>()
            : await customerLookup.FindNamesAsync(
                tenantId,
                quotations.Select(quotation => quotation.ClientId).Distinct().ToArray(),
                cancellationToken);

        // Misma idea que los nombres de cliente: una sola ida, con los ids sin repetir. Antes el
        // frontend se traía el padrón de miembros entero para poner un correo en cada fila.
        var advisorEmails = quotations.Count == 0
            ? new Dictionary<Guid, string?>()
            : await advisorLookup.FindEmailsAsync(
                tenantId,
                quotations.Select(quotation => quotation.AdvisorId.Value).Distinct().ToArray(),
                cancellationToken);

        return quotations
            .Select(quotation => quotation.ToListItemDto(
                clientNames.GetValueOrDefault(quotation.ClientId),
                advisorEmails.GetValueOrDefault(quotation.AdvisorId.Value)))
            .ToArray();
    }
}
