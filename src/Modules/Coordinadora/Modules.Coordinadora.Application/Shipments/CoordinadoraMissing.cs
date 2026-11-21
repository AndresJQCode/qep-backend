using BuildingBlocks.Domain;
using Modules.Coordinadora.Domain;

namespace Modules.Coordinadora.Application.Shipments;

/// <summary>
/// Un dato que falta para generar la guía (spec 2026-11-21 §5.3). <paramref name="Code"/> es contrato con la pantalla, que lo traduce:
/// <c>order_cancelled</c>, <c>store_pickup</c>, <c>nothing_to_ship</c>, <c>carrier_mismatch</c> (<paramref name="Label"/> = la transportadora
/// del pedido), <c>recipient_name</c>, <c>recipient_phone</c>, <c>recipient_address</c>, <c>recipient_city</c>,
/// <c>recipient_city_not_served</c> (<paramref name="Label"/> = el nombre de la ciudad), <c>sender_incomplete</c>,
/// <c>sender_city_not_served</c>, <c>package_defaults_missing</c> y <c>connection_missing</c>. Nunca lleva datos del destinatario.
/// </summary>
public sealed record CoordinadoraMissing(string Code, string? Reference, string? Label)
{
    public const string OrderCancelled = "order_cancelled";
    public const string StorePickup = "store_pickup";
    public const string NothingToShip = "nothing_to_ship";
    public const string CarrierMismatch = "carrier_mismatch";
    public const string RecipientName = "recipient_name";
    public const string RecipientPhone = "recipient_phone";
    public const string RecipientAddress = "recipient_address";
    public const string RecipientCity = "recipient_city";
    public const string RecipientCityNotServed = "recipient_city_not_served";
    public const string SenderIncomplete = CoordinadoraSettingsReadiness.SenderIncomplete;
    public const string SenderCityNotServed = "sender_city_not_served";
    public const string PackageDefaultsMissing = CoordinadoraSettingsReadiness.PackageDefaultsMissing;
    public const string ConnectionMissing = "connection_missing";

    public static CoordinadoraMissing Of(string code) => new(code, null, null);
}

/// <summary>Un faltante como viaja en <c>details.missing</c>.</summary>
public sealed record CoordinadoraMissingDto(string Code, string? Reference, string? Label);

/// <summary>422 <c>coordinadora.shipment.not_ready</c> (spec §5.4, paso 2): el pedido no está listo y <c>details.missing</c> trae <b>todo</b>
/// lo que falta, junto. Nada salió a Coordinadora.</summary>
public sealed class CoordinadoraShipmentNotReadyException(IReadOnlyList<CoordinadoraMissing> missing)
    : DomainException(CoordinadoraErrorCodes.ShipmentNotReady, "The order is not ready for a Coordinadora guide."),
      IHasProblemExtensions
{
    public IReadOnlyList<CoordinadoraMissing> Missing { get; } = missing;

    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["details"] = new { missing = missing.Select(value => new CoordinadoraMissingDto(value.Code, value.Reference, value.Label)).ToArray() },
    };
}
