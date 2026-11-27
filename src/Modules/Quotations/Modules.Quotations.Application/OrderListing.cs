using FluentValidation;
using FluentValidation.Results;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

internal static class OrderListing
{
    /// <summary>
    /// La zona de quien filtra, que el navegador manda como ID IANA. Desde el spec 2026-11-16 §4 ya no corta los días: el
    /// filtro va sobre la fecha del pedido, que es un día del tenant. Se sigue validando para no cambiar el contrato (un
    /// valor inválido sigue siendo <c>order.time_zone_invalid</c>). Sólo IANA, por lo mismo que <c>Tenant.ValidateTimeZone</c>:
    /// es lo que el navegador conoce, y un ID de Windows no lo manda ningún cliente nuestro.
    /// </summary>
    public static void EnsureValidTimeZone(string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            return;
        }

        try
        {
            if (TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim()).HasIanaId)
            {
                return;
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

    /// <summary>Spec 2026-11-10 §5: el filtro por envío; sin valor, todos.</summary>
    public static OrderFulfillmentStatus? ParseFulfillmentStatus(string? fulfillmentStatus)
    {
        if (string.IsNullOrWhiteSpace(fulfillmentStatus))
        {
            return null;
        }

        return Enum.TryParse<OrderFulfillmentStatus>(fulfillmentStatus, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "order.order.fulfillment_status_invalid",
                $"'{fulfillmentStatus}' is not a valid order fulfillment status.");
    }

    /// <summary>Spec 2026-11-10 §5: el filtro por la marca de facturado. Sólo <c>true</c> o <c>false</c>, sin
    /// distinguir mayúsculas; sin valor, todos.</summary>
    public static bool? ParseInvoiced(string? invoiced)
    {
        if (string.IsNullOrWhiteSpace(invoiced))
        {
            return null;
        }

        return bool.TryParse(invoiced, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "order.order.invoiced_invalid",
                $"'{invoiced}' is not a valid invoiced filter; use true or false.");
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

    /// <summary>
    /// El filtro <c>kind</c> de la lista y su Excel (spec 2026-11-26 §6.6 y §12-D): vacío y <c>All</c> traen todo, porque
    /// los pedidos de proyecto se cobran y facturan en la misma bandeja, y el módulo <c>projects</c> no se consulta. Por
    /// nombre y no con <c>Enum.TryParse</c>, que aceptaría «1». Otro valor es un 422 con el campo en <c>errors</c>.
    /// </summary>
    public static QuotationKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.Equals(kind.Trim(), "All", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Enum.GetNames<QuotationKind>()
            .FirstOrDefault(value => string.Equals(value, kind.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is not null
            ? Enum.Parse<QuotationKind>(name)
            : throw new ValidationException([new ValidationFailure("kind", "Use Standard, Project or All.")]);
    }

    /// <summary>
    /// Mayorista fase 3: el filtro por la marca de aprobación (D-1). <c>Pending</c> es <c>false</c> (sin aprobar) y
    /// <c>Approved</c> es <c>true</c>; sin valor, todos. Otro valor es un error de campo, como pide el contrato.
    /// </summary>
    public static bool? ParseWholesaleApproval(string? approval)
    {
        if (string.IsNullOrWhiteSpace(approval))
        {
            return null;
        }

        if (string.Equals(approval.Trim(), WholesaleApprovals.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(approval.Trim(), WholesaleApprovals.Approved, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new ValidationException(
            [new ValidationFailure(nameof(ListOrdersQuery.WholesaleApproval), "Use Pending or Approved.")]);
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
        IOrderRepository orderRepository,
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
        // Spec 2026-11-18 §5: la búsqueda no trae las líneas, así que se pregunta por toda la página de una vez.
        var requiringShipping = await orderRepository.FindIdsRequiringShippingAsync(
            tenantId, rows.Select(row => row.Id).ToArray(), cancellationToken);
        return rows
            .Select(row => row.ToListItemDto(
                row.QuotationId is { } quotationId ? quotationNumbers.GetValueOrDefault(quotationId) : null,
                clientNames.GetValueOrDefault(row.ClientId),
                advisors.GetValueOrDefault(row.AdvisorId.Value)?.Label,
                requiringShipping.Contains(row.Id.Value)))
            .ToArray();
    }
}
