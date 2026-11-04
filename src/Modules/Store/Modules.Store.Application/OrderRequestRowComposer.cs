using Modules.Store.Domain;

namespace Modules.Store.Application;

/// <summary>
/// Arma las filas de la bandeja de una página entera con una consulta de nombres y una de «ya es cliente», nunca una por fila.
/// </summary>
public sealed class OrderRequestRowComposer(IStoreMemberDirectory members, IStoreCustomerDirectory customers)
{
    public async Task<IReadOnlyList<StoreOrderRequestRowDto>> ComposeAsync(
        Guid tenantId, IReadOnlyList<StoreOrderRequest> requests, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        var memberIds = requests
            .SelectMany(request => new[] { request.ConvertedBy, request.DiscardedBy })
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var names = memberIds.Length == 0
            ? new Dictionary<Guid, string>()
            : (await members.NamesAsync(tenantId, memberIds, cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var known = await customers.KnownAsync(
            tenantId,
            requests.Select(request =>
                (request.Billing.PhoneE164, (string?)request.Billing.IdentificationType, (string?)request.Billing.IdentificationNumber))
                .ToArray(),
            cancellationToken);

        return requests.Select((request, index) => ToRow(request, known.Contains(index), names, now)).ToList();
    }

    public static StoreOrderRequestRowDto ToRow(
        StoreOrderRequest request, bool isKnown, IReadOnlyDictionary<Guid, string> names, DateTimeOffset now) =>
        new(
            request.Id,
            request.DisplayNumber,
            request.ReceivedAt,
            request.Status.ToString(),
            ToBilling(request.Billing),
            request.Shipping is null ? null : ToShipping(request.Shipping, request.Billing),
            request.CustomerId,
            isKnown,
            request.Lines.Count,
            request.Lines.Sum(line => line.Quantity),
            request.HasScaleDiscount,
            request.Total,
            request.Currency,
            request.Note,
            Member(request.ConvertedBy, names),
            request.ConvertedAt,
            request.OrderId,
            request.OrderNumber,
            request.TotalDifference,
            Member(request.DiscardedBy, names),
            request.DiscardedAt,
            request.DiscardReason?.ToString(),
            request.DiscardNote,
            request.IsBeingTaken(now));

    public static IReadOnlyList<StoreOrderRequestLineDto> ToLines(StoreOrderRequest request) =>
        request.Lines
            .OrderBy(line => line.Position)
            .Select(line => new StoreOrderRequestLineDto(
                line.VariantId, line.ProductId, line.Name, line.Quantity, line.UnitPrice, line.DiscountPercentage,
                line.DiscountedUnitPrice, line.LineTotal))
            .ToList();

    private static StoreMemberNameDto? Member(Guid? id, IReadOnlyDictionary<Guid, string> names) =>
        id is null ? null : new StoreMemberNameDto(id.Value, names.GetValueOrDefault(id.Value, "Sin nombre"));

    private static StoreRequestBillingDto ToBilling(StoreRequestBilling billing) =>
        new(
            billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Phone, billing.PhoneE164, billing.Email,
            billing.Address, billing.Place.Country, billing.Place.DepartmentId, billing.Place.DepartmentName, billing.Place.CityId,
            billing.Place.CityName);

    private static StoreRequestShippingDto ToShipping(StoreRequestShipping shipping, StoreRequestBilling billing) =>
        new(
            shipping.RecipientName, shipping.EffectiveRecipientName(billing), shipping.Phone, shipping.PhoneE164, shipping.Address,
            shipping.Place.Country, shipping.Place.DepartmentId, shipping.Place.DepartmentName, shipping.Place.CityId,
            shipping.Place.CityName);
}
