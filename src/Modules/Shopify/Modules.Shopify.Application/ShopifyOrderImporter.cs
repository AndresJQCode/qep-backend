using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §6.2: lleva una fila del espejo en espera a un pedido de QEP, o la deja en espera con el primer motivo que
/// falle. Sólo evalúa y escribe la fila; el commit final es del llamador (la ingesta, la conciliación o el reintento manual). La
/// única escritura que confirma por su cuenta es el cliente anotado en la fila antes del pedido (D-S24).
/// <list type="number">
/// <item>Un pedido de prueba se ignora sin mirar la tienda (§5.4). Uno cancelado antes de importarse también, salvo que el
/// pedido ya exista en QEP con su clave externa (se borró el espejo, o el worker cayó antes de guardar la fila): entonces lo
/// resuelve la reacción de §6.3 (<see cref="IShopifyOrderChangeReaction"/>), que pone la fila al día y lo anula.</item>
/// <item>Precondiciones en el orden de §6.2: tienda configurada, asesor activo, cuenta de cobro, moneda, cliente encontrado
/// activo, productos y, con un envío cobrado en COP, el IVA del envío del tenant (<c>freight_tax_rate_missing</c>).</item>
/// <item>Recién entonces se crea el cliente que no apareció, se anota en la fila y se guarda; después se escribe el pedido con su
/// envío (<see cref="ShopifyShippingMapper"/>) y su parte de envío, y con contraentrega se sincroniza el pago (D-S26).</item>
/// </list>
/// <para><b>Qué no se atrapa.</b> Lo que propaga el escritor (<c>RequestConcurrencyException</c>, <c>order.order.number_taken</c>,
/// base o red caídas) sale tal cual: la fila no cambia, el llamador no guarda y la entrega o la conciliación reintentan.
/// Un <see cref="ArgumentException"/> del adaptador (p. ej. el <c>ArgumentOutOfRangeException</c> de un modo de envío que no
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
    IShopifyShippingLookup shipping,
    IShopifyOrderWriter orders,
    IShopifyOrderChangeReaction changes,
    IShopifyCityCorrectionRepository cityCorrections,
    IShopifyCityCorrectionJobRepository cityCorrectionJobs,
    IShopifyUnresolvedCityReporter unresolvedCities,
    IShopifyCitySynonymLookup synonyms,
    IShopifyUnitOfWork unitOfWork,
    IClock clock) : IShopifyOrderImporter
{
    /// <summary>H24: el ancho de <c>orders.payment_method</c>.</summary>
    public const int PaymentMethodMaxLength = 50;

    /// <summary>§6.2.3 paso 2: el rótulo de la dirección cruda en las notas cuando no quedó como parte de envío.</summary>
    public const string RawAddressLabel = "Dirección de envío de Shopify: ";

    /// <summary>D-S6: el 1 % del total de Shopify.</summary>
    private const decimal TotalDifferenceTolerance = 0.01m;

    // Los detalles y los avisos viajan al frontend como JSON en camelCase, como los DTO de la API. Los títulos de Shopify y los
    // nombres de transportadora llevan tildes («Envío», «Inter Rapidísimo»): van tal cual y no como \u00ED, que es lo que la
    // bandeja muestra y lo que jsonb guarda igual.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

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
        if (snapshot.Test)
        {
            row.MarkIgnored();
            return new ShopifyImportOutcome(row.ImportState);
        }

        // Un cancelado puede tener ya su pedido en QEP aunque la fila sea nueva (el espejo se borró, o el worker cayó entre crear
        // el pedido y guardar la fila). Ignorarlo sin mirar dejaría ese pedido vivo: la reacción de §6.3 consulta la clave
        // externa y, si existe, pone la fila al día y lo anula; si no, la ignora. Con la tienda sin configurar, la propia reacción
        // deja la fila en espera con store_not_configured.
        if (snapshot.CancelledAt is not null)
        {
            await changes.ReactAsync(
                new ShopifyOrderChange(connection, store, row, ShopifyMirrorState.Of(row), snapshot), cancellationToken);
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

        var shippingResult = await ShippingAsync(tenantId, snapshot, orderCurrency, cancellationToken);
        if (shippingResult.WaitReason is { } shippingWait)
        {
            return Wait(
                row,
                shippingWait,
                new { shippingTitle = ShopifyShippingMapper.CarrierTitle(snapshot), amount = Normalized(ShopifyShippingMapper.Amount(snapshot)) },
                now);
        }

        var creation = found is null ? await customers.CreateAsync(store, snapshot, cancellationToken) : null;
        var customer = found ?? creation!.Customer;
        if (!customer.IsActive)
        {
            // El alta completa devuelve el dueño del documento si otra alta lo tomó entre la búsqueda y el commit (H8): puede
            // estar inactivo, y entonces aplica la misma regla que a uno encontrado.
            return Wait(row, ShopifyWaitReasons.CustomerInactive, new { customerId = customer.Id }, now);
        }

        // D-S24: el cliente se anota en su propia transacción antes del pedido. Si el pedido falla o el worker cae, el reintento
        // lo encuentra en la fila y no crea otro incompleto (uno sin teléfono ni correo no se volvería a encontrar). La huella del
        // cliente que nació completo por un sinónimo (O8) va en el mismo guardado, bajo el candado del sinónimo: un reintento usa el
        // cliente anotado y no la volvería a escribir.
        if (row.QepCustomerId != customer.Id)
        {
            row.AttachCustomer(customer.Id);
            if (creation is { Customer: { Created: true, IsComplete: true }, City: { SynonymId: { } customerSynonym } city })
            {
                await SaveImportCorrectionAsync(
                    ShopifyCityCorrection.ForCustomer(
                        tenantId, customerSynonym, ShopifyCityCorrectionOrigin.Import, customer.Id, row.Id, city.DepartmentId, city.CityId,
                        customer.Version, now),
                    cancellationToken);
            }
            else
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }

        var draft = shippingResult.Draft;
        // Con recogida no hay parte de envío, pero la ciudad igual se mira si fue la que frenó el alta completa del cliente.
        var shippingCity = snapshot.ShippingAddress is not null && (!draft.IsStorePickup || creation is { CityBlocked: true })
            ? await customers.ResolveCityAsync(snapshot.ShippingAddress, cancellationToken)
            : null;
        var party = draft.IsStorePickup ? null : ShippingParty(snapshot, shippingCity?.City);
        var addressUnresolved = !draft.IsStorePickup && snapshot.ShippingAddress is not null && party is null;
        var result = await orders.CreateAsync(
            Draft(connection, store, advisorId, account, customer, snapshot, lines, shippingResult, party, addressUnresolved),
            cancellationToken);
        if (result.IsRejected)
        {
            return Wait(row, ShopifyWaitReasons.OrderRejected, new { code = result.RejectedCode }, now);
        }

        await RecordShippingCityAsync(row, snapshot, draft, creation, party, shippingCity, result, now, cancellationToken);

        // El pedido ya existía con esa clave (el worker cayó entre crearlo y marcar la fila, o el segundo guardado de la
        // ingesta perdió la carrera). Es un éxito; lo que no se repite es el estado de pago, que nació con el del intento anterior
        // y Shopify pudo haber cambiado desde entonces. D-S26: un pedido contraentrega nace pendiente aunque Shopify ya lo dé por
        // pagado (H5), así que se sincroniza justo después de crearlo. Sincronizar con el mismo estado no hace nada.
        string? syncError = null;
        if ((result.AlreadyExisted || draft.CashOnDelivery) && snapshot.FinancialStatus.Length > 0)
        {
            var synced = await orders.SyncPaymentStatusAsync(tenantId, result.OrderId, snapshot.FinancialStatus, cancellationToken);
            // §6.3: payment_has_proofs o not_pending se auditan en la fila y no frenan: después de aprobar, el pago lo lleva QEP.
            syncError = synced.Kind == ShopifyOrderOutcomeKind.Ignored ? synced.Code : null;
        }

        // El cliente del pedido como quedó en Quotations: con AlreadyExisted es el del intento que lo creó, que puede no ser el que
        // este intento encontró (si su anotado se perdió). La fila tiene que apuntar al mismo cliente que el pedido.
        var totalDifference = snapshot.TotalPrice - result.Total;
        row.MarkImported(
            result.ClientId,
            result.OrderId,
            totalDifference,
            WarningsJson(
                customer,
                ShopifyCustomerResolver.DocumentOf(store, snapshot),
                addressUnresolved,
                ExceedsTolerance(totalDifference, snapshot.TotalPrice),
                shippingResult,
                draft.CarrierName),
            now);
        if (syncError is not null)
        {
            row.RecordLastError(syncError);
        }

        return new ShopifyImportOutcome(row.ImportState);
    }

    /// <summary>
    /// §6.2.2: cada SKU contra el <c>Code</c> de una variante activa del tenant (y de un producto activo). Se resuelven <b>todas</b> las líneas antes de decidir,
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
                product.VariantId,
                product.ProductId,
                line.Quantity,
                ShopifyPricing.UnitPriceWithVat(line.OriginalUnitPrice, snapshot.TaxesIncluded, product.TaxPercentage, currency),
                ShopifyPricing.DiscountPercentage(line.DiscountAmount, line.OriginalUnitPrice, line.Quantity),
                product.TaxPercentage));
        }

        return (lines, unmatched);
    }

    /// <summary>D-S23: la clave externa del pedido, <c>"{shopId}:{shopifyOrderId}"</c>. Con el shopId y no con la conexión, para
    /// que borrar y volver a enlazar la tienda no duplique pedidos. La usa también §6.3 (<see cref="ShopifyOrderChanges"/>).</summary>
    public static string ExternalKey(ShopifyConnectionRef connection, long shopifyOrderId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return $"{connection.ShopId}:{shopifyOrderId}";
    }

    /// <summary>§6.2.2 pasos 4 y 5: la transportadora del título de la primera línea y, sólo si el envío se cobra en COP, la tasa
    /// del envío del tenant. Con recogida o sin líneas no se pregunta nada.</summary>
    private async Task<ShopifyShippingResult> ShippingAsync(
        Guid tenantId, ShopifyOrderSnapshot snapshot, string currency, CancellationToken cancellationToken)
    {
        if (ShopifyShippingMapper.IsStorePickup(snapshot))
        {
            return ShopifyShippingMapper.Map(snapshot, currency, null, null);
        }

        var title = ShopifyShippingMapper.CarrierTitle(snapshot);
        var carrier = title is null ? null : await shipping.MatchCarrierAsync(tenantId, title, cancellationToken);
        var rate = ShopifyPricing.IsCop(currency) && ShopifyShippingMapper.Amount(snapshot) != 0m
            ? await shipping.GetFreightTaxPercentageAsync(tenantId, cancellationToken)
            : null;
        return ShopifyShippingMapper.Map(snapshot, currency, carrier, rate);
    }

    /// <summary>§6.2.3 paso 2 (H25): la dirección de Shopify es la parte de envío sólo si queda completa: nombre, teléfono,
    /// dirección, departamento y ciudad. El nombre y el teléfono, si la dirección no los trae, salen del comprador; el correo es
    /// el del comprador. La ciudad se resuelve con la misma regla que el alta del cliente (§6.2.1 paso 5).</summary>
    private static ShopifyPartyDraft? ShippingParty(ShopifyOrderSnapshot snapshot, ShopifyCityRef? city)
    {
        if (PartyFields(snapshot) is not { Name: { } name, Phone: { } phone, Address: { } line } fields || city is null)
        {
            return null;
        }

        return new ShopifyPartyDraft(name, phone, fields.Email, line, city.DepartmentId, city.CityId);
    }

    /// <summary>
    /// Spec 2026-10-26 §4.4: nombre, teléfono, dirección y correo de la parte de envío, con la regla del alta (<c>ShippingParty</c>)
    /// más el correo; <c>null</c> si falta alguno. Con estos campos y la ciudad, el pedido admite la parte de envío
    /// (<c>Order.EnsureCreatable</c> la pide completa, con correo: desvío S10).
    /// </summary>
    public static (string Name, string Phone, string Address, string Email)? ShippingPartyFields(ShopifyOrderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return PartyFields(snapshot) is { Name: { } name, Phone: { } phone, Address: { } line, Email: { } email }
            ? (name, phone, line, email)
            : null;
    }

    private static (string? Name, string? Phone, string? Address, string? Email)? PartyFields(ShopifyOrderSnapshot snapshot)
    {
        if (snapshot.ShippingAddress is not { } address)
        {
            return null;
        }

        var buyerName = string.Join(' ', new[] { snapshot.Buyer.FirstName, snapshot.Buyer.LastName }.Select(Blank).OfType<string>());
        return (
            Blank(address.Name) ?? Blank(buyerName),
            Blank(address.Phone) ?? Blank(snapshot.Buyer.Phone),
            Blank(string.Join(' ', new[] { address.Address1, address.Address2 }.Select(Blank).OfType<string>())),
            Blank(snapshot.Buyer.Email));
    }

    /// <summary>
    /// Spec 2026-10-26, antes de marcar la fila importada:
    /// <list type="bullet">
    /// <item>La parte de envío que casó por un sinónimo deja su huella <c>origin = import</c> (O8, D-C13) con la parte como la
    /// guardó Quotations, si no estaba ya: con <c>AlreadyExisted</c> el intento anterior pudo haberla guardado o perdido (desvío
    /// S3). Se guarda en el acto, bajo el candado del sinónimo (<see cref="SaveImportCorrectionAsync"/>).</item>
    /// <item>La ciudad de Colombia que no casó por ningún camino, con su departamento resuelto (D-C11), va a la bandeja si frenó
    /// algo: la parte de envío (no es recogida) o el alta completa del cliente. Si el reporte lanza, la importación lanza y la
    /// entrega se reintenta; la ocurrencia es idempotente por la fila del espejo. Un texto vacío (<c>Ignored</c>) no cambia nada
    /// acá.</item>
    /// <item>Un sinónimo que se confirmó entre el match y el reporte (<c>SynonymExists</c>) ya publicó su evento sin esta
    /// ocurrencia: el importador siembra los trabajos directo, con las reglas del evento
    /// (<see cref="ShopifyCitySynonymEventApplier.ResolveJobs"/>), sin mensaje de origen ni operador, en el guardado final.</item>
    /// </list>
    /// </summary>
    private async Task RecordShippingCityAsync(
        ShopifyOrderMirror row,
        ShopifyOrderSnapshot snapshot,
        ShopifyShippingDraft draft,
        ShopifyCustomerCreation? creation,
        ShopifyPartyDraft? party,
        ShopifyCityResolution? shippingCity,
        ShopifyOrderWriteResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Con AlreadyExisted la parte guardada puede ser otra (alguien la cambió de ciudad): sólo es del sinónimo si tiene su ciudad.
        if (party is not null
            && shippingCity?.City is { SynonymId: { } partySynonym } matched
            && result.ShippingParty is { } persisted
            && persisted.CityId == matched.CityId
            && await cityCorrections.FindAsync(
                row.TenantId, partySynonym, ShopifyCityCorrectionTarget.Order, result.OrderId, cancellationToken) is null)
        {
            await SaveImportCorrectionAsync(
                ShopifyCityCorrection.ForOrder(
                    row.TenantId, partySynonym, ShopifyCityCorrectionOrigin.Import, result.OrderId, row.Id, persisted, now),
                cancellationToken);
        }

        if (shippingCity is not { City: null, UnmatchedDepartmentId: { } departmentId, CityText: { } cityText }
            || (draft.IsStorePickup && creation is not { CityBlocked: true }))
        {
            return;
        }

        var bornIncomplete = creation is { Customer: { Created: true, IsComplete: false } } ? creation.Customer.Id : (Guid?)null;
        var report = new ShopifyUnresolvedCityReport(
            row.TenantId,
            row.Id,
            result.OrderId,
            bornIncomplete,
            departmentId,
            cityText,
            ShopifyUnresolvedCitySample.Of(snapshot.ShippingAddress!),
            Blank(snapshot.ShippingAddress!.ProvinceCode),
            PartyCityOnly: !draft.IsStorePickup && ShippingPartyFields(snapshot) is not null,
            CustomerCityOnly: creation is { CityBlocked: true },
            now);
        var reported = await unresolvedCities.ReportAsync(report, cancellationToken);
        if (reported is { Outcome: ShopifyUnresolvedCityOutcome.SynonymExists, SynonymId: { } synonymId })
        {
            foreach (var job in ShopifyCitySynonymEventApplier.ResolveJobs(
                row.TenantId, synonymId, row.Id, result.OrderId, report.CustomerId, report.PartyCityOnly, report.CustomerCityOnly,
                operatorSubjectId: Guid.Empty, sourceMessageId: null, now))
            {
                cityCorrectionJobs.Add(job);
            }
        }
    }

    /// <summary>
    /// Spec 2026-10-26 D-C6: la huella <c>origin = import</c> se guarda como la del worker que corrige, en su propia transacción y
    /// bajo el candado del sinónimo, que es el mismo con que el evento <c>corrected</c> lista las huellas. Con el candado tomado se
    /// relee el sinónimo: si ya no apunta a la ciudad que casó (el operador lo corrigió durante la importación), el evento pudo
    /// listar antes de esta huella, así que la recorrección se siembra acá, sin operador ni mensaje. Guarda también lo que la
    /// importación tenga pendiente (el cliente anotado en la fila); el commit final sigue siendo del llamador.
    /// </summary>
    private async Task SaveImportCorrectionAsync(ShopifyCityCorrection correction, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockCitySynonymAsync(correction.SynonymId, cancellationToken);
        cityCorrections.Add(correction);
        if (await synonyms.FindAsync(correction.SynonymId, cancellationToken) is { IsDeleted: false } current
            && current.CityId != correction.CityId)
        {
            cityCorrectionJobs.Add(ShopifyCityCorrectionJob.Recorrect(correction, Guid.Empty, sourceMessageId: null, clock.UtcNow));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// §6.2.3: el borrador del pedido, con el envío ya resuelto (§6.2.2) y la parte de envío si la dirección quedó completa. Una
    /// dirección que no quedó completa va cruda a las notas, para que nadie tenga que abrir Shopify para despachar.
    /// <para>Una línea con cantidad 0 (un payload sin <c>quantity</c>) no se filtra ni se corrige acá: la regla del
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
        IReadOnlyList<ShopifyOrderLineDraft> lines,
        ShopifyShippingResult shippingResult,
        ShopifyPartyDraft? party,
        bool addressUnresolved) =>
        new(
            store.TenantId,
            snapshot.Name,
            customer.Id,
            advisorId,
            PaymentMethod(snapshot.PaymentGateways),
            Notes(snapshot, addressUnresolved ? snapshot.ShippingAddress : null),
            party,
            account,
            lines,
            snapshot.FinancialStatus.Length == 0 ? null : snapshot.FinancialStatus,
            snapshot.CreatedAt,
            shippingResult.Draft,
            ExternalKey(connection, snapshot.OrderId),
            shippingResult.UnrecognizedTitle);

    /// <summary>§6.2.3 paso 2 (H24): las pasarelas unidas con <c>", "</c> y recortadas al ancho de la columna.</summary>
    private static string? PaymentMethod(IReadOnlyList<string> gateways)
    {
        var joined = string.Join(", ", gateways.Select(gateway => gateway?.Trim()).Where(gateway => !string.IsNullOrEmpty(gateway)));
        return joined.Length == 0 ? null : joined.Length <= PaymentMethodMaxLength ? joined : joined[..PaymentMethodMaxLength].TrimEnd();
    }

    /// <summary>§6.2.3 paso 2: <c>Shopify #1001</c> y, en otra línea, la nota del comprador; si la dirección no quedó como parte
    /// de envío, otra línea con la dirección cruda. Sin tope (H24).</summary>
    private static string Notes(ShopifyOrderSnapshot snapshot, ShopifyAddress? unresolvedAddress)
    {
        var parts = new List<string> { $"Shopify {snapshot.Name}" };
        if (Blank(snapshot.Note) is { } note)
        {
            parts.Add(note);
        }

        if (unresolvedAddress is { } address && RawAddress(address) is { Length: > 0 } raw)
        {
            parts.Add($"{RawAddressLabel}{raw}");
        }

        return string.Join('\n', parts);
    }

    /// <summary>La dirección cruda de la línea de las notas, sin el rótulo. No es la muestra de la bandeja: esa no lleva nombre ni
    /// teléfono (<see cref="ShopifyUnresolvedCitySample"/>).</summary>
    private static string RawAddress(ShopifyAddress address) =>
        string.Join(", ", new[]
            {
                address.Name, address.Address1, address.Address2, address.City, address.Province, address.CountryCode, address.Phone,
            }
            .Select(Blank)
            .OfType<string>());

    /// <summary>D-S6: la diferencia avisa sólo si supera el 1 % del total de Shopify, en cualquier sentido. Con un total de 0,
    /// cualquier diferencia avisa.</summary>
    internal static bool ExceedsTolerance(decimal difference, decimal shopifyTotal) =>
        Math.Abs(difference) > Math.Abs(shopifyTotal) * TotalDifferenceTolerance;

    /// <summary>15000.00 y 15000 son el mismo valor; el detalle viaja sin los ceros que el parser conserva del texto de
    /// Shopify, para que no cambie según venga del REST o del GraphQL.</summary>
    private static decimal Normalized(decimal value) => value / 1.000000000000000000000000000m;

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>§6.2.4: los avisos como arreglo de <c>{ code, detail }</c>, en el orden del spec. <c>customer_incomplete</c> lleva
    /// el documento que sí vino, para que quien complete la ficha lo tenga a la mano (§6.2.1 paso 4);
    /// <c>carrier_unrecognized</c>, el título de Shopify, y <c>cash_on_delivery_unmatched</c>, la transportadora reconocida que
    /// no recauda (§6.2.2). <c>shipping_address_unresolved</c> y <c>total_difference</c> no llevan detalle: la dirección cruda va
    /// en las notas del pedido y la diferencia en <c>total_difference</c> de la fila.</summary>
    internal static string? WarningsJson(
        ShopifyCustomerRef? customer,
        ShopifyBuyerDocument? document,
        bool addressUnresolved,
        bool totalDiffers,
        ShopifyShippingResult? shippingResult,
        string? carrierName)
    {
        var warnings = new List<ShopifyImportWarning>();
        if (customer is { IsComplete: false })
        {
            warnings.Add(new ShopifyImportWarning(
                ShopifyWarnings.CustomerIncomplete,
                document is null ? null : new { document = new { type = document.Type, number = document.Number } }));
        }

        if (addressUnresolved)
        {
            warnings.Add(new ShopifyImportWarning(ShopifyWarnings.ShippingAddressUnresolved, null));
        }

        if (totalDiffers)
        {
            warnings.Add(new ShopifyImportWarning(ShopifyWarnings.TotalDifference, null));
        }

        if (shippingResult?.UnrecognizedTitle is { } title)
        {
            warnings.Add(new ShopifyImportWarning(ShopifyWarnings.CarrierUnrecognized, new { shippingTitle = title }));
        }

        if (shippingResult?.CashOnDeliveryUnmatched == true)
        {
            warnings.Add(new ShopifyImportWarning(ShopifyWarnings.CashOnDeliveryUnmatched, new { carrierName }));
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
