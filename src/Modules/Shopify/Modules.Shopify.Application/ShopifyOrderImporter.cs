using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §6.2: lleva una fila del espejo en espera a un pedido de QEP, o la deja en espera con el primer motivo que
/// falle. Sólo evalúa y escribe la fila; el commit final es del llamador (la ingesta, la conciliación o el reintento manual). La
/// única escritura que confirma por su cuenta es el cliente anotado en la fila antes del pedido (D-S24).
/// <list type="number">
/// <item>Un pedido de prueba o cancelado antes de importarse se ignora sin mirar la tienda (§5.4).</item>
/// <item>Precondiciones en el orden de §6.2: tienda configurada, asesor activo, cuenta de cobro, moneda, cliente encontrado
/// activo y productos. <c>freight_tax_rate_missing</c> llega con el envío (tarea 14).</item>
/// <item>Recién entonces se crea el cliente que no apareció, se anota en la fila y se guarda; después se escribe el pedido.</item>
/// </list>
/// <para><b>Qué no se atrapa.</b> Lo que propaga el escritor (<c>RequestConcurrencyException</c>, <c>order.order.number_taken</c>,
/// base o red caídas) sale tal cual: la fila no cambia, el llamador no guarda y la entrega o la conciliación reintentan (ruling
/// S17). Un <see cref="ArgumentException"/> del adaptador (p. ej. el <c>ArgumentOutOfRangeException</c> de un modo de envío que no
/// es <c>Charged</c> ni <c>Free</c>) es un error de programación del importador, no un dato de la tienda: también sale tal cual,
/// fuera del <c>Guarded</c> de la ingesta, así que el worker lo reintenta y lo deja en su log en cada intento, y al agotarlos cierra
/// la entrega con el tipo y el mensaje en <c>last_error</c>. Volverlo <c>Waiting</c> o <c>ShopifyPayloadException</c> lo
/// escondería como si fuera culpa de la tienda.</para>
/// </summary>
public sealed class ShopifyOrderImporter(
    IShopifyMemberDirectory members,
    IShopifyBillingAccounts billingAccounts,
    ShopifyCustomerResolver customers,
    IShopifyProductLookup products,
    IShopifyOrderWriter orders,
    IShopifyUnitOfWork unitOfWork,
    IClock clock) : IShopifyOrderImporter
{
    /// <summary>H24: el ancho de <c>orders.payment_method</c>.</summary>
    public const int PaymentMethodMaxLength = 50;

    private const string Cop = "COP";

    // Los detalles y los avisos viajan al frontend como JSON en camelCase, como los DTO de la API.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ShopifyImportOutcome> ImportAsync(
        ShopifyConnectionRef connection,
        ShopifyStore store,
        ShopifyOrderMirror row,
        ShopifyOrderSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(snapshot);
        // Las búsquedas van con el tenant de la fila y el alta del cliente y el borrador con el de la tienda: un llamador que
        // mezcle tienda, fila y conexión de tenants o tiendas distintos escribiría en otro tenant. Es un error de programación,
        // no un pedido en espera, así que lanza antes de tocar nada.
        if (store.TenantId != row.TenantId || store.ConnectionId != row.ConnectionId
            || connection.TenantId != row.TenantId || connection.ConnectionId != row.ConnectionId)
        {
            throw new ArgumentException(
                "The Shopify store, the mirror row and the connection must belong to the same tenant and connection.", nameof(store));
        }

        if (row.ImportState != ShopifyImportState.Waiting)
        {
            return new ShopifyImportOutcome(row.ImportState);
        }

        // §5.4: QEP no trae pedidos de prueba ni los cancelados antes de importarse. Se guardan para la bandeja, con filtro.
        if (snapshot.Test || snapshot.CancelledAt is not null)
        {
            row.MarkIgnored();
            return new ShopifyImportOutcome(row.ImportState);
        }

        var now = clock.UtcNow;
        if (store is not { IsConfigured: true, AdvisorMembershipId: { } advisorId, Billing: { } billing })
        {
            return Wait(row, ShopifyWaitReasons.StoreNotConfigured, null, now);
        }

        var tenantId = row.TenantId;
        if (!await members.IsActiveMemberAsync(tenantId, advisorId, cancellationToken))
        {
            return Wait(row, ShopifyWaitReasons.AdvisorInactive, null, now);
        }

        var account = await billingAccounts.FindAsync(
            tenantId, billing.CompanyId, billing.BankName, billing.AccountNumber, cancellationToken);
        if (account is null)
        {
            return Wait(row, ShopifyWaitReasons.BillingAccountMissing, null, now);
        }

        var orderCurrency = snapshot.Currency.Trim().ToUpperInvariant();
        var billingCurrency = account.Currency.Trim().ToUpperInvariant();
        if (!string.Equals(orderCurrency, billingCurrency, StringComparison.Ordinal))
        {
            return Wait(row, ShopifyWaitReasons.CurrencyMismatch, new { orderCurrency, billingCurrency }, now);
        }

        var found = await customers.FindAsync(store, row, snapshot, cancellationToken);
        if (found is { IsActive: false })
        {
            return Wait(row, ShopifyWaitReasons.CustomerInactive, new { customerId = found.Id }, now);
        }

        var (lines, unmatched) = await ResolveLinesAsync(tenantId, snapshot, orderCurrency, cancellationToken);
        if (unmatched.Count > 0)
        {
            return Wait(row, ShopifyWaitReasons.ProductUnmatched, new { skus = unmatched }, now);
        }

        var customer = found ?? await customers.CreateAsync(store, snapshot, cancellationToken);
        if (!customer.IsActive)
        {
            // El alta completa devuelve el dueño del documento si otra alta lo tomó entre la búsqueda y el commit (H8): puede
            // estar inactivo, y entonces aplica la misma regla que a uno encontrado.
            return Wait(row, ShopifyWaitReasons.CustomerInactive, new { customerId = customer.Id }, now);
        }

        // D-S24: el cliente se anota en su propia transacción antes del pedido. Si el pedido falla o el worker cae, el reintento
        // lo encuentra en la fila y no crea otro incompleto (uno sin teléfono ni correo no se volvería a encontrar).
        if (row.QepCustomerId != customer.Id)
        {
            row.AttachCustomer(customer.Id);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var result = await orders.CreateAsync(
            Draft(connection, store, advisorId, account, customer, snapshot, lines), cancellationToken);
        if (result.IsRejected)
        {
            return Wait(row, ShopifyWaitReasons.OrderRejected, new { code = result.RejectedCode }, now);
        }

        // S17: el pedido ya existía con esa clave (el worker cayó entre crearlo y marcar la fila, o el segundo guardado de la
        // ingesta perdió la carrera). Es un éxito; lo que no se repite es el estado de pago, que nació con el del intento anterior
        // y Shopify pudo haber cambiado desde entonces. Sincronizar con el mismo estado no hace nada (D-S26).
        if (result.AlreadyExisted && snapshot.FinancialStatus.Length > 0)
        {
            await orders.SyncPaymentStatusAsync(tenantId, result.OrderId, snapshot.FinancialStatus, cancellationToken);
        }

        // El cliente del pedido como quedó en Quotations: con AlreadyExisted es el del intento que lo creó, que puede no ser el que
        // este intento encontró (si su anotado se perdió). La fila tiene que apuntar al mismo cliente que el pedido.
        row.MarkImported(
            result.ClientId,
            result.OrderId,
            snapshot.TotalPrice - result.Total,
            WarningsJson(customer, ShopifyCustomerResolver.DocumentOf(store, snapshot)),
            now);
        return new ShopifyImportOutcome(row.ImportState);
    }

    /// <summary>
    /// §6.2.2: cada SKU contra un <c>Product.Code</c> activo del tenant. Se resuelven <b>todas</b> las líneas antes de decidir,
    /// para que <c>wait_detail.skus</c> traiga la lista completa. Una línea sin SKU entra a la lista como <c>null</c>: es lo que
    /// trajo, y el frontend la muestra como «línea sin SKU». El SKU se compara recortado (los espacios de la tienda no son parte
    /// del código).
    /// </summary>
    private async Task<(IReadOnlyList<ShopifyOrderLineDraft> Lines, IReadOnlyList<string?> Unmatched)> ResolveLinesAsync(
        Guid tenantId, ShopifyOrderSnapshot snapshot, string currency, CancellationToken cancellationToken)
    {
        var skus = snapshot.Lines
            .Select(line => line.Sku?.Trim())
            .Where(sku => !string.IsNullOrEmpty(sku))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var found = skus.Length == 0
            ? new Dictionary<string, ShopifyProductRef>(StringComparer.Ordinal)
            : await products.FindByCodesAsync(tenantId, skus, cancellationToken);

        var unmatched = new List<string?>();
        var lines = new List<ShopifyOrderLineDraft>(snapshot.Lines.Count);
        foreach (var line in snapshot.Lines)
        {
            var sku = line.Sku?.Trim();
            if (string.IsNullOrEmpty(sku) || !found.TryGetValue(sku, out var product) || !product.IsActive)
            {
                var listed = string.IsNullOrEmpty(sku) ? null : sku;
                if (!unmatched.Contains(listed))
                {
                    unmatched.Add(listed);
                }

                continue;
            }

            lines.Add(new ShopifyOrderLineDraft(
                product.Id,
                line.Quantity,
                UnitPriceWithVat(line.OriginalUnitPrice, snapshot.TaxesIncluded, product.TaxPercentage, currency),
                DiscountPercentage(line.DiscountAmount, line.OriginalUnitPrice, line.Quantity),
                product.TaxPercentage));
        }

        return (lines, unmatched);
    }

    /// <summary>
    /// §6.2.3: el borrador del pedido. En esta tarea va sin envío (<c>ShippingDetails.None</c>) ni parte de envío; la tarea 14
    /// agrega el envío, la parte con la dirección resuelta, la contraentrega y la dirección cruda en las notas.
    /// <para>Una línea con cantidad 0 (un payload sin <c>quantity</c>, tarea 10) no se filtra ni se corrige acá: la regla del
    /// pedido la rechaza (<c>order.item.quantity_invalid</c>) y la fila queda <c>order_rejected</c> con ese código, a la vista. Un
    /// precio 0 sí entra: en Shopify es un regalo legítimo, y si en cambio era un precio ausente, el total de Shopify no cuadra con
    /// el de QEP y lo delata <c>total_difference</c>.</para>
    /// </summary>
    private static ShopifyOrderDraft Draft(
        ShopifyConnectionRef connection,
        ShopifyStore store,
        Guid advisorId,
        ShopifyBillingAccountRef account,
        ShopifyCustomerRef customer,
        ShopifyOrderSnapshot snapshot,
        IReadOnlyList<ShopifyOrderLineDraft> lines) =>
        new(
            store.TenantId,
            snapshot.Name,
            customer.Id,
            advisorId,
            PaymentMethod(snapshot.PaymentGateways),
            Notes(snapshot),
            ShippingParty: null,
            account,
            lines,
            snapshot.FinancialStatus.Length == 0 ? null : snapshot.FinancialStatus,
            snapshot.CreatedAt,
            Shipping: null,
            // D-S23: con el shopId y no con la conexión, para que borrar y volver a enlazar la tienda no duplique pedidos.
            $"{connection.ShopId}:{snapshot.OrderId}",
            ExternalShippingTitle: null);

    /// <summary>§6.2.2 (H3, D-S27): con impuestos incluidos el precio va tal cual; si no, en COP se le suma el IVA del producto y
    /// fuera de COP va tal cual, porque ahí la tasa se fuerza a 0.</summary>
    private static decimal UnitPriceWithVat(decimal original, bool taxesIncluded, int taxPercentage, string currency) =>
        taxesIncluded || !string.Equals(currency, Cop, StringComparison.Ordinal)
            ? original
            : original * (100 + taxPercentage) / 100m;

    /// <summary>§6.2.2: la suma de las asignaciones de descuento sobre <c>originalUnitPrice × quantity</c>, en porcentaje con 4
    /// decimales. Sin base (cantidad o precio 0) no hay porcentaje que calcular.</summary>
    private static decimal DiscountPercentage(decimal discount, decimal originalUnitPrice, decimal quantity)
    {
        var gross = originalUnitPrice * quantity;
        return gross <= 0m || discount <= 0m ? 0m : Math.Round(discount / gross * 100m, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>§6.2.3 paso 2 (H24): las pasarelas unidas con <c>", "</c> y recortadas al ancho de la columna.</summary>
    private static string? PaymentMethod(IReadOnlyList<string> gateways)
    {
        var joined = string.Join(", ", gateways.Select(gateway => gateway?.Trim()).Where(gateway => !string.IsNullOrEmpty(gateway)));
        return joined.Length == 0 ? null : joined.Length <= PaymentMethodMaxLength ? joined : joined[..PaymentMethodMaxLength].TrimEnd();
    }

    /// <summary>§6.2.3 paso 2: <c>Shopify #1001</c> y, en otra línea, la nota del comprador. Sin tope (H24).</summary>
    private static string Notes(ShopifyOrderSnapshot snapshot)
    {
        var header = $"Shopify {snapshot.Name}";
        var note = snapshot.Note?.Trim();
        return string.IsNullOrEmpty(note) ? header : $"{header}\n{note}";
    }

    /// <summary>§6.2.4: los avisos como arreglo de <c>{ code, detail }</c>. <c>customer_incomplete</c> lleva el documento que sí
    /// vino, para que quien complete la ficha lo tenga a la mano (§6.2.1 paso 4); los demás avisos los agrega la tarea 14.</summary>
    private static string? WarningsJson(ShopifyCustomerRef customer, ShopifyBuyerDocument? document)
    {
        var warnings = new List<ShopifyImportWarning>();
        if (!customer.IsComplete)
        {
            warnings.Add(new ShopifyImportWarning(
                ShopifyWarnings.CustomerIncomplete,
                document is null ? null : new { document = new { type = document.Type, number = document.Number } }));
        }

        return warnings.Count == 0 ? null : JsonSerializer.Serialize(warnings, Json);
    }

    private static ShopifyImportOutcome Wait(ShopifyOrderMirror row, string reason, object? detail, DateTimeOffset now)
    {
        row.MarkWaiting(reason, detail is null ? null : JsonSerializer.Serialize(detail, Json), now);
        return new ShopifyImportOutcome(row.ImportState);
    }
}

/// <summary>Un aviso de §6.2.4 tal como se guarda en <c>shopify.orders.warnings</c>: el código del diccionario
/// (<see cref="ShopifyWarnings"/>) y su detalle, o <c>null</c> si no tiene. Así cada aviso lleva sus datos sin que el frontend
/// tenga que adivinar de dónde salen.</summary>
public sealed record ShopifyImportWarning(string Code, object? Detail);
