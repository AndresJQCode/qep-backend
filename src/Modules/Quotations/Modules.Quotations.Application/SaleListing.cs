using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Lo que el listado de ventas y su Excel comparten fuera de la consulta: cómo se leen los filtros
/// que llegan como texto y cómo se completa cada fila. Mismo papel que
/// <see cref="QuotationListing"/>: la tabla y el archivo no pueden empezar a contar cosas distintas.
/// </summary>
internal static class SaleListing
{
    // Texto libre por query string: un valor que no es del enum es un 422 con código de dominio,
    // no un filtro que en silencio no devuelve nada ni un 500 de un cast.
    public static SaleStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return Enum.TryParse<SaleStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "sale.sale.status_invalid",
                $"'{status}' is not a valid sale status.");
    }

    public static SalePaymentStatus? ParsePaymentStatus(string? paymentStatus)
    {
        if (string.IsNullOrWhiteSpace(paymentStatus))
        {
            return null;
        }

        return Enum.TryParse<SalePaymentStatus>(paymentStatus, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "sale.sale.payment_status_invalid",
                $"'{paymentStatus}' is not a valid sale payment status.");
    }

    // Sin término, null ("sin filtro"); con término que no resolvió a ningún cliente, vacío, y la
    // búsqueda ya sabe que no hay nada que traer.
    public static async Task<IReadOnlyCollection<Guid>?> ResolveClientIdsByCucAsync(
        IQuotationCustomerLookup customerLookup,
        Guid tenantId,
        string? clientCuc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientCuc))
        {
            return null;
        }

        var matchedIds = await customerLookup.SearchIdsByCucAsync(tenantId, clientCuc, cancellationToken);
        return matchedIds.ToArray();
    }

    public static async Task<IReadOnlyList<SaleListItemDto>> ToListItemsAsync(
        IQuotationCustomerLookup customerLookup,
        IQuotationAdvisorLookup advisorLookup,
        Guid tenantId,
        IReadOnlyList<SaleWithQuotation> rows,
        CancellationToken cancellationToken)
    {
        // Una ida para los nombres y otra para los correos, con los ids sin repetir.
        var clientNames = rows.Count == 0
            ? new Dictionary<Guid, string>()
            : await customerLookup.FindNamesAsync(
                tenantId,
                rows.Select(row => row.Quotation.ClientId).Distinct().ToArray(),
                cancellationToken);

        // El correo y no el nombre, mismo criterio que el listado de cotizaciones (spec 2026-09-11, D1).
        var advisors = rows.Count == 0
            ? new Dictionary<Guid, QuotationAdvisor>()
            : await advisorLookup.FindAsync(
                tenantId,
                rows.Select(row => row.Quotation.AdvisorId.Value).Distinct().ToArray(),
                cancellationToken);

        return rows
            .Select(row => row.ToListItemDto(
                clientNames.GetValueOrDefault(row.Quotation.ClientId),
                advisors.GetValueOrDefault(row.Quotation.AdvisorId.Value)?.Email))
            .ToArray();
    }
}
