using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal static class OrderListing
{
    public static OrderStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "order.order.status_invalid",
                $"'{status}' is not a valid order status.");
    }

    public static OrderPaymentStatus? ParsePaymentStatus(string? paymentStatus)
    {
        if (string.IsNullOrWhiteSpace(paymentStatus))
        {
            return null;
        }

        return Enum.TryParse<OrderPaymentStatus>(paymentStatus, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "order.order.payment_status_invalid",
                $"'{paymentStatus}' is not a valid order payment status.");
    }

    /// <summary>Spec 2026-10-15 §6.1: el filtro por origen del pedido; sin valor, todos.</summary>
    public static OrderSource? ParseSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        return Enum.TryParse<OrderSource>(source, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "order.order.source_invalid",
                $"'{source}' is not a valid order source.");
    }

    // El CUC no vive en el pedido: se resuelve a ids de cliente contra Customers y recién entonces se
    // filtra. Sin término no se consulta nada.
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

    /// <summary>Las filas de la lista: cliente y asesor del pedido resueltos en una consulta cada uno,
    /// y el número de la cotización de origen para las que la tienen.</summary>
    public static async Task<IReadOnlyList<OrderListItemDto>> ToListItemsAsync(
        IQuotationCustomerLookup customerLookup,
        IQuotationAdvisorLookup advisorLookup,
        IQuotationRepository quotationRepository,
        Guid tenantId,
        IReadOnlyList<Order> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var clientNames = await customerLookup.FindNamesAsync(
            tenantId, rows.Select(row => row.ClientId).Distinct().ToArray(), cancellationToken);
        var advisors = await advisorLookup.FindAsync(
            tenantId, rows.Select(row => row.AdvisorId.Value).Distinct().ToArray(), cancellationToken);
        var quotationIds = rows
            .Where(row => row.QuotationId is not null)
            .Select(row => row.QuotationId!.Value)
            .Distinct()
            .ToArray();
        var quotationNumbers = quotationIds.Length == 0
            ? new Dictionary<QuotationId, string>()
            : await quotationRepository.FindNumbersAsync(tenantId, quotationIds, cancellationToken);
        return rows
            .Select(row => row.ToListItemDto(
                row.QuotationId is { } quotationId ? quotationNumbers.GetValueOrDefault(quotationId) : null,
                clientNames.GetValueOrDefault(row.ClientId),
                advisors.GetValueOrDefault(row.AdvisorId.Value)?.Label))
            .ToArray();
    }
}
