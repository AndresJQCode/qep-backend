using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Los motivos de un envío <c>Failed</c> (spec 2026-11-14 §5, plan D14). Contrato con el diccionario del frontend.</summary>
public static class ShopifyPushFailureCodes
{
    public const string AccessDenied = "access_denied";

    public const string UserError = "user_error";

    public const string OrderNotFound = "order_not_found";

    public const string ConnectionMissing = "connection_missing";

    public const string Rejected = "rejected";

    public const string RetriesExhausted = "retries_exhausted";
}

/// <summary>
/// Spec 2026-11-14 §5 y plan D12, D14: manda un envío a Shopify con <c>orderUpdate</c>. Los valores salen del pedido de QEP y de la
/// fila del espejo <b>al mandar</b>, no al encolar: el envío siempre lleva lo último.
/// <list type="bullet">
/// <item>Sólo los grupos del envío (Review Focus 4): un envío de la dirección no pisa la nota de Shopify con un <c>BuyerNotes</c>
/// vacío (los pedidos importados antes no lo tienen) ni los atributos.</item>
/// <item>Dirección: la parte de QEP, con el nombre en <c>firstName</c> y la dirección en <c>address1</c>; la ciudad y el departamento
/// con los nombres de QEP y su <c>provinceCode</c>; la empresa y el país del <b>último</b> snapshot, porque <c>orderUpdate</c>
/// reemplaza la dirección entera (Review Focus 3).</item>
/// <item>Nota: <c>BuyerNotes</c> (vacía la borra en Shopify). Atributos: los de QEP o, sin corrección, los del último snapshot.</item>
/// </list>
/// Conexión ausente o no operativa → <c>connection_missing</c>; token vencido (<c>AccessToken</c> nulo, lo refresca otro worker) →
/// reintentar sin llamar a Shopify; pedido o fila ausente → <c>order_not_found</c>. Con <c>Updated</c> el envío queda <c>Sent</c> y con
/// <c>Failed</c>, <c>Failed</c> con su código, en un guardado; con <c>Retry</c> no se escribe nada (la espera la da el lease).
/// </summary>
public sealed class ShopifyOrderPushProcessor(
    IShopifyConnectionDirectory connections,
    IShopifyOrderWriter orders,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyGeographyLookup geography,
    IShopifyOrderUpdater updater,
    IShopifyUnitOfWork unitOfWork,
    IClock clock)
{
    private const string Colombia = "CO";

    private static readonly ShopifyOrderUpdateResult RetryLater = new(ShopifyOrderUpdateKind.Retry, null, null);

    public async Task<ShopifyOrderUpdateResult> ProcessAsync(ShopifyOrderPush push, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(push);
        var connection = await connections.FindAsync(push.TenantId, push.ConnectionId, cancellationToken);
        if (connection is not { IsOperational: true })
        {
            return await FailAsync(push, ShopifyPushFailureCodes.ConnectionMissing, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(connection.AccessToken))
        {
            return RetryLater;
        }

        var row = await mirrors.FindByIdAsync(push.TenantId, push.MirrorOrderId, cancellationToken);
        var order = await orders.ReadStateAsync(push.TenantId, push.QepOrderId, cancellationToken);
        if (row is null || order is null)
        {
            return await FailAsync(push, ShopifyPushFailureCodes.OrderNotFound, cancellationToken);
        }

        var update = await BuildAsync(push, row, order, cancellationToken);
        var result = update.ShippingAddress is null && !update.SetNote && update.Attributes is null
            // Nada que mandar: la dirección del envío ya no existe en el pedido. No es un error de Shopify.
            ? new ShopifyOrderUpdateResult(ShopifyOrderUpdateKind.Updated, null, null)
            : await updater.UpdateAsync(connection, update, cancellationToken);
        switch (result.Kind)
        {
            case ShopifyOrderUpdateKind.Updated:
                push.MarkSent(clock.UtcNow);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                break;
            case ShopifyOrderUpdateKind.Failed:
                await FailAsync(push, result.FailureCode ?? ShopifyPushFailureCodes.Rejected, cancellationToken);
                break;
        }

        return result;
    }

    private async Task<ShopifyOrderUpdate> BuildAsync(
        ShopifyOrderPush push, ShopifyOrderMirror row, ShopifyOrderState order, CancellationToken cancellationToken)
    {
        var latest = ShopifyOrderIngestion.ReadPayload(row.Payload);
        ShopifyAddressUpdate? address = null;
        if (push.Groups.Contains(ShopifyPushGroup.ShippingAddress) && order.ShippingParty is { } party)
        {
            var names = await geography.DescribeCityAsync(party.DepartmentId, party.CityId, cancellationToken)
                ?? throw new InvalidOperationException("The city of the shipping party is not in Geography.");
            address = new ShopifyAddressUpdate(
                party.Name ?? string.Empty,
                string.Empty,
                party.Address ?? string.Empty,
                string.Empty,
                names.City,
                ColombiaProvinceCodes.CodeFor(names.Department),
                Blank(latest.ShippingAddress?.CountryCode) ?? Colombia,
                party.Phone ?? string.Empty,
                Blank(latest.ShippingAddress?.Company));
        }

        var setNote = push.Groups.Contains(ShopifyPushGroup.Note);
        var attributes = push.Groups.Contains(ShopifyPushGroup.Attributes)
            ? row.QepAttributes is { } edited ? ShopifyEditPlanner.ReadAttributes(edited) : latest.Attributes
            : null;
        return new ShopifyOrderUpdate(row.ShopifyOrderId, address, setNote, setNote ? order.BuyerNotes : null, attributes);
    }

    private async Task<ShopifyOrderUpdateResult> FailAsync(ShopifyOrderPush push, string code, CancellationToken cancellationToken)
    {
        push.MarkFailed(code, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ShopifyOrderUpdateResult(ShopifyOrderUpdateKind.Failed, code, null);
    }

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
