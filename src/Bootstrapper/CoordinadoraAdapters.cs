using Modules.Coordinadora.Application;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-21 §5.1: las conexiones de Coordinadora de Integrations, por el puerto de Coordinadora. Calcado de
/// <see cref="SiigoConnectionDirectory"/>: <c>IIntegrationConnections</c> ya filtra tenant, estado, visibilidad y clave legible; acá se traduce a
/// la causa (<see cref="ConnectionReasonCodes"/>) y se arma <see cref="CoordinadoraCredentials"/>. Una conexión de otro proveedor es
/// <c>not_found</c>. Nunca imprime una clave.
/// </summary>
internal sealed class CoordinadoraConnectionDirectory(IIntegrationConnectionRepository repository, IIntegrationConnections connections)
    : ICoordinadoraConnectionDirectory
{
    private static string Provider => IntegrationProviders.Coordinadora.Key;

    public async Task<CoordinadoraConnectionInfo?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken);
        return connection is not null
            && connection.TenantId == tenantId
            && string.Equals(connection.ProviderKey, Provider, StringComparison.Ordinal)
                ? new CoordinadoraConnectionInfo(connection.Id, connection.Name, connection.Status.ToString())
                : null;
    }

    public async Task<CoordinadoraConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var resolution = await connections.ResolveAsync(tenantId, connectionId, cancellationToken);
        if (resolution.Connection is not { } connection)
        {
            return new CoordinadoraConnectionResolution(null, ConnectionReasonCodes.Of(resolution.Unavailable!.Value));
        }

        if (!string.Equals(connection.ProviderKey, Provider, StringComparison.Ordinal))
        {
            return new CoordinadoraConnectionResolution(null, ConnectionReasonCodes.Of(ConnectionUnavailableReason.NotFound));
        }

        return connection.Fields.GetValueOrDefault(CoordinadoraFieldKeys.Nit) is { Length: > 0 } nit
            && connection.Fields.GetValueOrDefault(CoordinadoraFieldKeys.ClientId) is { Length: > 0 } clientId
            && connection.Fields.GetValueOrDefault(CoordinadoraFieldKeys.GuidesUser) is { Length: > 0 } guidesUser
            && connection.Secrets.GetValueOrDefault(CoordinadoraFieldKeys.ApiKey) is { Length: > 0 } apiKey
            && connection.Secrets.GetValueOrDefault(CoordinadoraFieldKeys.ApiPassword) is { Length: > 0 } apiPassword
            && connection.Secrets.GetValueOrDefault(CoordinadoraFieldKeys.GuidesPassword) is { Length: > 0 } guidesPassword
                ? new CoordinadoraConnectionResolution(
                    new CoordinadoraCredentials(connection.Id, nit, apiKey, apiPassword, clientId, guidesUser, guidesPassword), null)
                : new CoordinadoraConnectionResolution(null, ConnectionReasonCodes.Incomplete);
    }

    public async Task<IReadOnlyList<CoordinadoraConnectionInfo>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        [
            .. (await connections.ListByProviderAsync(tenantId, Provider, cancellationToken))
                .Select(listing => new CoordinadoraConnectionInfo(listing.Id, listing.Name, listing.Status.ToString())),
        ];
}

/// <summary>Lee <c>City.DivipolaCode</c> y <c>City.CoordinadoraName</c> de Geography (el código de Coordinadora es DIVIPOLA + <c>000</c>). No
/// decide nada: una ciudad que no existe es <c>null</c>, y el armador lo reporta como <c>recipient_city</c>.</summary>
internal sealed class CoordinadoraGeographyLookup(ICityRepository cityRepository) : ICoordinadoraGeographyLookup
{
    public async Task<CoordinadoraCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken)
    {
        var city = await cityRepository.FindAsync(new CityId(cityId), cancellationToken);
        return city is null || string.IsNullOrWhiteSpace(city.DivipolaCode)
            ? null
            : new CoordinadoraCityRef(city.Id.Value, city.Name, city.DivipolaCode.Trim(), city.CoordinadoraName);
    }
}

/// <summary>El adaptador de <see cref="ICoordinadoraOrderSource"/> (spec 2026-11-21 §5.7) sobre <see cref="IExternalOrderShipping"/> de
/// Quotations. Vive en Bootstrapper porque Coordinadora no referencia a Quotations (<c>CoordinadoraLayerTests</c>): copia campo por campo.</summary>
internal sealed class CoordinadoraOrderSource(IExternalOrderShipping shipping) : ICoordinadoraOrderSource
{
    public async Task<CoordinadoraShipmentInput?> GetAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await shipping.GetForShippingAsync(tenantId, orderId, cancellationToken);
        return order is null ? null : Translate(order);
    }

    internal static CoordinadoraShipmentInput Translate(ExternalShippingOrder order) =>
        new(
            order.OrderId,
            order.OrderNumber,
            order.Today,
            order.IsCancelled,
            order.IsStorePickup,
            order.RequiresShipping,
            order.CarrierName,
            order.CashOnDelivery,
            order.ProductsTotal,
            order.NetTotal,
            order.Notes,
            order.Version,
            [.. order.Lines.Select(line => new CoordinadoraShipmentLine(line.Name, line.Code, line.Quantity))],
            new CoordinadoraRecipientInput(
                order.Recipient.Name,
                order.Recipient.Phone,
                order.Recipient.Address,
                order.Recipient.CityId,
                order.Recipient.Country,
                order.Recipient.CityName,
                order.Recipient.Identification));
}
