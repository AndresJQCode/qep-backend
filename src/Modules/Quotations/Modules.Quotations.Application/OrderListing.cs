using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Lo que el listado de pedidos y su Excel comparten fuera de la consulta: cómo se leen los filtros
/// que llegan como texto y cómo se completa cada fila. Mismo papel que
/// <see cref="QuotationListing"/>: la tabla y el archivo no pueden empezar a contar cosas distintas.
/// </summary>
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

    public static async Task<IReadOnlyList<OrderListItemDto>> ToListItemsAsync(
        IQuotationCustomerLookup customerLookup,
        IQuotationAdvisorLookup advisorLookup,
        Guid tenantId,
        IReadOnlyList<OrderWithQuotation> rows,
        CancellationToken cancellationToken)
    {
        // Una ida para los clientes y otra para las asesoras, con los ids sin repetir.
        var clientNames = rows.Count == 0
            ? new Dictionary<Guid, string>()
            : await customerLookup.FindNamesAsync(
                tenantId,
                rows.Select(row => row.Quotation.ClientId).Distinct().ToArray(),
                cancellationToken);

        // El nombre con respaldo al correo (QuotationAdvisor.Label), igual que QuotationListing: la
        // tabla y el Excel de pedidos presentan a la asesora como los de cotizaciones (spec
        // 2026-09-11, D1, nota del 2026-09-15).
        var advisors = rows.Count == 0
            ? new Dictionary<Guid, QuotationAdvisor>()
            : await advisorLookup.FindAsync(
                tenantId,
                rows.Select(row => row.Quotation.AdvisorId.Value).Distinct().ToArray(),
                cancellationToken);

        return rows
            .Select(row => row.ToListItemDto(
                clientNames.GetValueOrDefault(row.Quotation.ClientId),
                advisors.GetValueOrDefault(row.Quotation.AdvisorId.Value)?.Label))
            .ToArray();
    }
}
