using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

internal static class OrderListing
{
    /// <summary>
    /// El calendario con que se cortan los días del filtro: el de la zona de quien filtra, que el
    /// navegador manda como ID IANA, o el del tenant si no la manda. Un pedido creado a la 01:00 del
    /// 15 en Madrid es del 15 para quien filtra desde Madrid, aunque en Colombia todavía fuera el 14
    /// (pedido del owner, 2026-10-15). Sólo IANA, por lo mismo que <c>Tenant.ValidateTimeZone</c>: es
    /// lo que el navegador conoce, y un ID de Windows no lo manda ningún cliente nuestro.
    /// </summary>
    public static TenantCalendar CalendarFor(TenantCalendar tenantCalendar, string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            return tenantCalendar;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim());
            if (zone.HasIanaId)
            {
                return new TenantCalendar(tenantCalendar.UtcNow, zone);
            }
        }
        catch (TimeZoneNotFoundException)
        {
        }

        throw new QuotationsDomainException(
            "order.time_zone_invalid",
            "timeZone must be a valid IANA time zone.");
    }

    // Texto libre por query string: un valor que no es del enum es un 422 con código de dominio,
    // no un filtro que en silencio no devuelve nada ni un 500 de un cast.
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
