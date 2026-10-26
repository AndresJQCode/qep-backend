using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Domain;
using Modules.Companies.Domain;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Integrations.Domain;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-15 §5.2, §6.2 y §6.3: los adaptadores de los puertos de Shopify. Cada uno traduce y respeta el
/// <c>tenantId</c>; lo que decide el importador (tareas 13 y 14) no se prueba acá.
/// </summary>
public sealed class ShopifyAdaptersTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Transportadoras e IVA del envío (§6.2.2 pasos 4 y 5) ----

    [Fact]
    public async Task ShippingLookupMatchesNameAndExternalNameNormalized()
    {
        var express = Carrier.Create(CarrierId.New(), TenantId, "Envía", ["Coordinadora Express"], true, 1, Now);
        var lookup = ShippingLookup(new FakeCarrierRepository(express));

        var byExternal = await lookup.MatchCarrierAsync(TenantId, "  coordinadora   EXPRESS ", Ct);
        var byName = await lookup.MatchCarrierAsync(TenantId, "ENVIA", Ct);

        Assert.Equal(new ShopifyCarrierRef(express.Id.Value, "Envía", true), byExternal);
        Assert.Equal(new ShopifyCarrierRef(express.Id.Value, "Envía", true), byName);
        Assert.Null(await lookup.MatchCarrierAsync(TenantId, "Servientrega", Ct));
        Assert.Null(await lookup.MatchCarrierAsync(TenantId, "   ", Ct));
    }

    [Fact]
    public async Task ShippingLookupIgnoresInactiveCarriers()
    {
        var inactive = Carrier.Create(CarrierId.New(), TenantId, "Coordinadora", [], true, 1, Now);
        inactive.Deactivate(Now);
        var lookup = ShippingLookup(new FakeCarrierRepository(inactive));

        Assert.Null(await lookup.MatchCarrierAsync(TenantId, "Coordinadora", Ct));
    }

    [Fact]
    public async Task ShippingLookupInactiveRateReturnsNull()
    {
        var rate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA 19", 19, Now);
        var taxRates = new FakeTaxRateRepository(rate);
        var lookup = ShippingLookup(new FakeCarrierRepository(), new FakeQuotationSettingsStore(rate.Id.Value), taxRates);
        Assert.Equal(19, await lookup.GetFreightTaxPercentageAsync(TenantId, Ct));

        rate.Deactivate(Now);

        Assert.Null(await lookup.GetFreightTaxPercentageAsync(TenantId, Ct));
        Assert.Null(await ShippingLookup(new FakeCarrierRepository(), new FakeQuotationSettingsStore(null), taxRates)
            .GetFreightTaxPercentageAsync(TenantId, Ct));
    }

    // ---- Productos (§6.2.2; variantes 2026-10-20 §2.9) ----

    // La variante por defecto lleva el id del producto: un pedido de un producto sin opciones empareja igual que antes.
    [Fact]
    public async Task ProductLookupReadsTaxPercentageByRateId()
    {
        var rate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA 19", 19, Now);
        var taxed = NewProduct("SH-400", rate.Id);
        var untaxed = NewProduct("SH-200", null);
        untaxed.Deactivate(Now);
        var lookup = new ShopifyProductLookup(new FakeProductRepository(taxed, untaxed), new FakeTaxRateRepository(rate));

        var found = await lookup.FindByCodesAsync(TenantId, ["SH-400", "SH-200", "NO-EXISTE"], Ct);

        Assert.Equal(new ShopifyProductRef(taxed.Id.Value, taxed.Id.Value, "SH-400", true, 19), found["SH-400"]);
        Assert.Equal(new ShopifyProductRef(untaxed.Id.Value, untaxed.Id.Value, "SH-200", false, 0), found["SH-200"]);
        Assert.False(found.ContainsKey("NO-EXISTE"));
    }

    // El SKU de la línea es el Code de la variante: «CAM-L» es la L, no la primera variante del producto. Sensible a
    // mayúsculas, como el índice único: «cam-l» no empareja. Una variante inactiva vuelve, marcada.
    [Fact]
    public async Task ShopifyLookupMatchesVariantCodeExactly()
    {
        var rate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA 19", 19, Now);
        var world = VariantWorld.Create(TenantId, Now, taxRateId: rate.Id, largeIsActive: false);
        var lookup = new ShopifyProductLookup(new FakeProductRepository(world.Product), new FakeTaxRateRepository(rate));

        var found = await lookup.FindByCodesAsync(TenantId, ["CAM-M", "CAM-L", "cam-l"], Ct);

        Assert.Equal(new ShopifyProductRef(world.Medium.Id.Value, world.Product.Id.Value, "CAM-M", true, 19), found["CAM-M"]);
        Assert.Equal(new ShopifyProductRef(world.Large.Id.Value, world.Product.Id.Value, "CAM-L", false, 19), found["CAM-L"]);
        Assert.NotEqual(world.Product.Id.Value, found["CAM-L"].VariantId);
        Assert.False(found.ContainsKey("cam-l"));
    }

    [Fact]
    public async Task ShopifyLookupIgnoresVariantsOfAnotherTenant()
    {
        var world = VariantWorld.Create(Guid.CreateVersion7(), Now);
        var lookup = new ShopifyProductLookup(new FakeProductRepository(world.Product), new FakeTaxRateRepository());

        var found = await lookup.FindByCodesAsync(TenantId, ["CAM-M"], Ct);

        Assert.Empty(found);
    }

    // ---- Pedido (§6.2.3 y §6.3) ----

    [Fact]
    public async Task OrderWriterMapsExternalKeyTakenToExisting()
    {
        var existingClient = Guid.CreateVersion7();
        var writer = new FakeExternalOrderWriter
        {
            OnCreate = _ => new ExternalOrderResult(Guid.CreateVersion7(), "PED-7", 134_000m, 112_605m, "COP", existingClient, AlreadyExisted: true)
        };

        var draft = Draft();

        var result = await OrderWriter(writer).CreateAsync(draft, Ct);

        Assert.False(result.IsRejected);
        Assert.True(result.AlreadyExisted);
        // El cliente del pedido que ya existía, no el del borrador: pueden diferir si el anotado de un intento anterior se perdió.
        Assert.NotEqual(draft.ClientId, result.ClientId);
        Assert.Equal(existingClient, result.ClientId);
        Assert.Equal("PED-7", result.OrderNumber);
        Assert.Equal(134_000m, result.Total);
    }

    // Spec 2026-10-26 D-C4: la huella de la parte de envío es la que guardó Quotations (normalizada), también con AlreadyExisted;
    // sin parte, no hay huella.
    [Fact]
    public async Task OrderWriterReturnsThePersistedShippingParty()
    {
        var (departmentId, cityId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var persisted = new ExternalShippingParty("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", departmentId, cityId);
        var writer = new FakeExternalOrderWriter
        {
            OnCreate = draft => new ExternalOrderResult(
                Guid.CreateVersion7(), "PED-8", 119_000m, 100_000m, "COP", draft.ClientId, AlreadyExisted: true, Shipping: persisted),
        };

        var result = await OrderWriter(writer).CreateAsync(Draft(), Ct);
        var withoutParty = await OrderWriter(new FakeExternalOrderWriter()).CreateAsync(Draft(), Ct);

        Assert.Equal(
            new ShopifyShippingFingerprint("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", departmentId, cityId),
            result.ShippingParty);
        Assert.Null(withoutParty.ShippingParty);
    }

    // §6.3: la fila en espera que se canceló se busca por clave externa antes de ignorarla. El adaptador busca sólo pedidos de
    // origen Shopify y devuelve el cliente y el total del pedido guardado.
    [Fact]
    public async Task OrderWriterFindByExternalKeyReturnsTheShopifyOrder()
    {
        var clientId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var writer = new FakeExternalOrderWriter();
        writer.ByExternalKey[(TenantId, OrderSource.Shopify, "954889:5550000000001")] =
            new ExternalOrderResult(orderId, "PED-9", 119_000m, 100_000m, "COP", clientId, AlreadyExisted: true);

        var found = await OrderWriter(writer).FindByExternalKeyAsync(TenantId, "954889:5550000000001", Ct);
        var missing = await OrderWriter(writer).FindByExternalKeyAsync(TenantId, "954889:otro", Ct);

        Assert.NotNull(found);
        Assert.Equal(
            (orderId, "PED-9", 119_000m, true, clientId, false),
            (found.OrderId, found.OrderNumber, found.Total, found.AlreadyExisted, found.ClientId, found.IsRejected));
        Assert.Null(missing);
    }

    [Fact]
    public async Task OrderWriterDomainExceptionReturnsRejectedWithCode()
    {
        var writer = new FakeExternalOrderWriter
        {
            OnCreate = _ => throw new QuotationsDomainException("order.order.cash_on_delivery_pending_only", "No.")
        };

        var result = await OrderWriter(writer).CreateAsync(Draft(), Ct);

        Assert.True(result.IsRejected);
        Assert.Equal("order.order.cash_on_delivery_pending_only", result.RejectedCode);
    }

    [Fact]
    public async Task OrderWriterNotFoundReturnsRejectedWithCode()
    {
        var writer = new FakeExternalOrderWriter
        {
            OnCreate = _ => throw new ResourceNotFoundException("quotation.billing.company_not_found", "No.")
        };

        var result = await OrderWriter(writer).CreateAsync(Draft(), Ct);

        Assert.Equal("quotation.billing.company_not_found", result.RejectedCode);
    }

    [Fact]
    public async Task OrderWriterCreateTranslatesTheDraft()
    {
        var writer = new FakeExternalOrderWriter();
        var carrierId = Guid.CreateVersion7();
        var shippingParty = new ShopifyPartyDraft("Laura Gómez", "+573001234567", "laura@verde.co", "Cra 1 # 2-3", Guid.CreateVersion7(), Guid.CreateVersion7());
        var draft = Draft() with
        {
            FinancialStatus = "paid",
            ShippingParty = shippingParty,
            Shipping = new ShopifyShippingDraft(false, carrierId, "Coordinadora", "Charged", 15_000m, 19, false),
            ExternalShippingTitle = null,
        };

        var result = await OrderWriter(writer).CreateAsync(draft, Ct);

        var sent = Assert.Single(writer.Created);
        Assert.Equal(OrderSource.Shopify, sent.Source);
        Assert.Equal(TenantId, sent.TenantId);
        Assert.Equal("#1001", sent.ExternalReference);
        Assert.Equal("123:456", sent.ExternalKey);
        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, sent.PaymentStatus);
        Assert.Equal(new ShippingDetails(carrierId, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, false), sent.Shipping);
        Assert.Equal(new QuotationBillingAccountRequest(draft.BillingAccount.CompanyId, "Bancolombia", "CTA-1", "COP"), sent.BillingAccount);
        Assert.NotNull(sent.Parties);
        Assert.Null(sent.Parties.Billing);
        Assert.False(sent.Parties.IsStorePickup);
        Assert.Equal(
            new QuotationPartyRequest("Laura Gómez", null, "+573001234567", "laura@verde.co", "Cra 1 # 2-3", shippingParty.DepartmentId, shippingParty.CityId),
            sent.Parties.Shipping);
        Assert.NotEqual(draft.Lines[0].VariantId, draft.Lines[0].ProductId);
        Assert.Equal([new ExternalOrderLine(draft.Lines[0].VariantId, draft.Lines[0].ProductId, 2m, 11_900m, 10.5m, 19)], sent.Lines);
        Assert.False(result.AlreadyExisted);
        Assert.Equal(writer.LastResult!.OrderId, result.OrderId);
    }

    [Fact]
    public async Task OrderWriterStorePickupHasNoShippingAndNoShippingParty()
    {
        var writer = new FakeExternalOrderWriter();
        var draft = Draft() with
        {
            ShippingParty = new ShopifyPartyDraft("Laura", null, null, "Cra 1", Guid.CreateVersion7(), Guid.CreateVersion7()),
            Shipping = new ShopifyShippingDraft(true, null, null, null, 0m, 0, false),
        };

        await OrderWriter(writer).CreateAsync(draft, Ct);

        var sent = Assert.Single(writer.Created);
        Assert.True(sent.Parties!.IsStorePickup);
        Assert.Null(sent.Parties.Shipping);
        Assert.Equal(ShippingDetails.None, sent.Shipping);
    }

    // §6.2.3 paso 4 (H5): con contraentrega el pedido nace PaymentPending aunque Shopify ya lo dé por pagado; el
    // importador lo sincroniza después.
    [Fact]
    public async Task OrderWriterCashOnDeliveryIsBornPending()
    {
        var writer = new FakeExternalOrderWriter();
        var draft = Draft() with
        {
            FinancialStatus = "paid",
            Shipping = new ShopifyShippingDraft(false, null, null, null, 0m, 0, true),
        };

        await OrderWriter(writer).CreateAsync(draft, Ct);

        Assert.Equal(OrderPaymentStatus.PaymentPending, Assert.Single(writer.Created).PaymentStatus);
    }

    [Theory]
    [InlineData("paid", OrderPaymentStatus.FullPaymentReceived)]
    [InlineData(" PARTIALLY_PAID ", OrderPaymentStatus.PartialPaymentReceived)]
    [InlineData("pending", OrderPaymentStatus.PaymentPending)]
    [InlineData("authorized", OrderPaymentStatus.PaymentPending)]
    public async Task OrderWriterSyncMapsTheFinancialStatus(string financialStatus, OrderPaymentStatus expected)
    {
        var writer = new FakeExternalOrderWriter();
        var orderId = Guid.CreateVersion7();

        var outcome = await OrderWriter(writer).SyncPaymentStatusAsync(TenantId, orderId, financialStatus, Ct);

        Assert.Equal(ShopifyOrderOutcome.Applied, outcome);
        Assert.Equal([(TenantId, orderId, expected)], writer.Synced);
    }

    [Theory]
    [InlineData("refunded")]
    [InlineData("partially_refunded")]
    [InlineData("voided")]
    public async Task OrderWriterSyncRefundedOrVoidedIsUnchangedWithoutCodeAndWithoutCalling(string financialStatus)
    {
        var writer = new FakeExternalOrderWriter();

        var outcome = await OrderWriter(writer).SyncPaymentStatusAsync(TenantId, Guid.CreateVersion7(), financialStatus, Ct);

        // Spec §6.3: «no cambian nada». Sin código, así que no hay nada que llevar a last_error.
        Assert.Equal(ShopifyOrderOutcome.Unchanged, outcome);
        Assert.Equal(ShopifyOrderOutcomeKind.Unchanged, outcome.Kind);
        Assert.Null(outcome.Code);
        Assert.Equal(0, writer.Calls);
    }

    // «Al cobro» nunca sale de la tienda (§6.2.2 paso 3), y un número o una combinación de banderas no son un modo.
    [Theory]
    [InlineData("OnDelivery")]
    [InlineData("onDelivery")]
    [InlineData("5")]
    [InlineData("Charged,Free")]
    [InlineData("Gratis")]
    public async Task OrderWriterCreateRejectsAChargeModeTheStoreCannotGive(string chargeMode)
    {
        var writer = new FakeExternalOrderWriter();
        var draft = Draft() with { Shipping = new ShopifyShippingDraft(false, null, null, chargeMode, 0m, 0, false) };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => OrderWriter(writer).CreateAsync(draft, Ct));

        Assert.Equal(0, writer.Calls);
    }

    [Theory]
    [InlineData("Free", FreightChargeMode.Free)]
    [InlineData("charged", FreightChargeMode.Charged)]
    public async Task OrderWriterCreateAcceptsChargedAndFree(string chargeMode, FreightChargeMode expected)
    {
        var writer = new FakeExternalOrderWriter();
        var amount = expected == FreightChargeMode.Charged ? 10_000m : 0m;
        var draft = Draft() with { Shipping = new ShopifyShippingDraft(false, null, null, chargeMode, amount, 19, false) };

        await OrderWriter(writer).CreateAsync(draft, Ct);

        Assert.Equal(expected, Assert.Single(writer.Created).Shipping!.ChargeMode);
    }

    // El consecutivo lo tomó otra alta a la vez (IX de número, QuotationsUnitOfWork): es transitorio, no una regla del pedido.
    // Se propaga para que la entrega o la conciliación reintenten, en vez de dejar la fila en order_rejected.
    [Fact]
    public async Task OrderWriterCreateNumberTakenPropagates()
    {
        var writer = new FakeExternalOrderWriter
        {
            OnCreate = _ => throw new QuotationsDomainException("order.order.number_taken", "Taken.")
        };

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => OrderWriter(writer).CreateAsync(Draft(), Ct));

        Assert.Equal("order.order.number_taken", error.Code);
    }

    [Fact]
    public async Task OrderWriterConcurrencyConflictPropagates()
    {
        var writer = new FakeExternalOrderWriter
        {
            OnSync = () => throw new RequestConcurrencyException("concurrency.conflict", "Conflict."),
            OnCancel = () => throw new RequestConcurrencyException("concurrency.conflict", "Conflict.")
        };

        await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => OrderWriter(writer).SyncPaymentStatusAsync(TenantId, Guid.CreateVersion7(), "paid", Ct));
        await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => OrderWriter(writer).CancelAsync(TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), "Cancelado en Shopify: other", Ct));
    }

    [Theory]
    [InlineData("order.order.not_pending")]
    [InlineData("order.order.payment_has_proofs")]
    public async Task OrderWriterSyncNotPendingOrWithProofsReturnsIgnored(string code)
    {
        var writer = new FakeExternalOrderWriter { OnSync = () => throw new QuotationsDomainException(code, "No.") };

        var outcome = await OrderWriter(writer).SyncPaymentStatusAsync(TenantId, Guid.CreateVersion7(), "paid", Ct);

        Assert.Equal(ShopifyOrderOutcome.Ignored(code), outcome);
    }

    [Fact]
    public async Task OrderWriterCancelAlreadyInvoicedReturnsOutcome()
    {
        var writer = new FakeExternalOrderWriter
        {
            OnCancel = () => throw new QuotationsDomainException("order.order.already_invoiced", "No.")
        };

        var outcome = await OrderWriter(writer).CancelAsync(TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), "Cancelado en Shopify: customer", Ct);

        Assert.Equal(ShopifyOrderOutcome.AlreadyInvoiced, outcome);
    }

    [Fact]
    public async Task OrderWriterCancelAlreadyCancelledReturnsOutcome()
    {
        var writer = new FakeExternalOrderWriter
        {
            OnCancel = () => throw new QuotationsDomainException("order.order.already_cancelled", "No.")
        };

        var outcome = await OrderWriter(writer).CancelAsync(TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), "Cancelado en Shopify: customer", Ct);

        Assert.Equal(ShopifyOrderOutcome.AlreadyCancelled, outcome);
    }

    [Fact]
    public async Task OrderWriterCancelAppliedPassesTheAdvisorAndReason()
    {
        var writer = new FakeExternalOrderWriter();
        var orderId = Guid.CreateVersion7();
        var advisor = Guid.CreateVersion7();

        var outcome = await OrderWriter(writer).CancelAsync(TenantId, orderId, advisor, "Cancelado en Shopify: customer", Ct);

        Assert.Equal(ShopifyOrderOutcome.Applied, outcome);
        Assert.Equal([(TenantId, orderId, advisor, "Cancelado en Shopify: customer")], writer.Cancelled);
    }

    // IExternalOrderWriter descarta TODO el tracker de Quotations ante la carrera de la clave
    // externa. Cada llamada corre en su propio scope, así que nada que el llamador tenga pendiente en Quotations se pierde.
    [Fact]
    public async Task OrderWriterRunsEachCallInItsOwnScope()
    {
        var instances = new List<FakeExternalOrderWriter>();
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var instance = new FakeExternalOrderWriter();
            instances.Add(instance);
            return instance;
        });
        services.AddScoped<IExternalOrderWriter>(provider => provider.GetRequiredService<FakeExternalOrderWriter>());
        using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var callerScope = root.CreateScope();
        var callers = callerScope.ServiceProvider.GetRequiredService<FakeExternalOrderWriter>();
        var writer = new ShopifyOrderWriter(callerScope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

        await writer.CreateAsync(Draft(), Ct);
        await writer.SyncPaymentStatusAsync(TenantId, Guid.CreateVersion7(), "paid", Ct);
        await writer.CancelAsync(TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), "Cancelado en Shopify: other", Ct);

        Assert.Equal(0, callers.Calls);
        Assert.Equal(4, instances.Count);
        Assert.All(instances.Skip(1), instance => Assert.Equal(1, instance.Calls));
    }

    // ---- Clientes (§6.2.1) ----

    [Fact]
    public async Task CustomerDirectoryUnknownDocumentTypeIsNotCalled()
    {
        var customers = new FakeCustomerRepository();
        var directory = new ShopifyCustomerDirectory(customers, new FakeCustomerMergeStore(), new FakeCustomerProvisioning(), new PassThroughPhones());

        Assert.Null(await directory.FindByDocumentAsync(TenantId, "RUT", "1020304050", Ct));
        Assert.Null(await directory.FindByDocumentAsync(TenantId, "CC", "   ", Ct));
        Assert.Equal(0, customers.Calls);
    }

    [Fact]
    public async Task CustomerDirectoryFindsByDocumentEmailPhoneAndIdWithItsState()
    {
        var incomplete = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), TenantId, "Laura", "+573001234567", "laura@verde.co", "CO", Now, new PassThroughPhones());
        incomplete.Deactivate(Now);
        var customers = new FakeCustomerRepository(incomplete)
        {
            Identifications = { [(IdentificationType.Cc, "1020304050")] = incomplete.Id }
        };
        var directory = new ShopifyCustomerDirectory(customers, new FakeCustomerMergeStore(), new FakeCustomerProvisioning(), new PassThroughPhones());
        var expected = new ShopifyCustomerRef(incomplete.Id.Value, false, false);

        Assert.Equal(expected, await directory.FindByDocumentAsync(TenantId, "cc", " 1020304050 ", Ct));
        Assert.Equal(expected, await directory.FindByEmailAsync(TenantId, "LAURA@verde.co", Ct));
        Assert.Equal(expected, await directory.FindByPhoneAsync(TenantId, "+573001234567", "CO", Ct));
        Assert.Equal(expected, await directory.FindByIdAsync(TenantId, incomplete.Id.Value, Ct));
        Assert.Null(await directory.FindByIdAsync(Guid.CreateVersion7(), incomplete.Id.Value, Ct));
    }

    // §6.2.1 paso 2: el teléfono de la dirección de envío lo escribe el comprador a mano; se busca en E.164 con el país de la
    // dirección (sin él, CO), con el mismo normalizador con el que Customers guardó phone_e164.
    [Fact]
    public async Task CustomerDirectoryNormalizesThePhoneWithTheCountry()
    {
        var customer = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), TenantId, "Laura", "+573001234567", null, "CO", Now, new PassThroughPhones());
        var customers = new FakeCustomerRepository(customer);
        var phones = new MappedPhones { [("300 123 4567", "CO")] = "+573001234567" };
        var directory = new ShopifyCustomerDirectory(customers, new FakeCustomerMergeStore(), new FakeCustomerProvisioning(), phones);

        Assert.Equal(customer.Id.Value, (await directory.FindByPhoneAsync(TenantId, " 300 123 4567 ", null, Ct))?.Id);
        Assert.Equal(customer.Id.Value, (await directory.FindByPhoneAsync(TenantId, "300 123 4567", "co", Ct))?.Id);
        var calls = customers.Calls;
        Assert.Null(await directory.FindByPhoneAsync(TenantId, "no es un teléfono", "CO", Ct));
        Assert.Equal(calls, customers.Calls);
    }

    // Spec 2026-10-24 §3.3: un pedido de Shopify que reusa el qep_customer_id de un fusionado cae en el que quedó.
    [Fact]
    public async Task FindByIdOfAMergedReturnsTheSurvivor()
    {
        var (survivor, merged) = MergedPair();
        var directory = new ShopifyCustomerDirectory(
            new FakeCustomerRepository(survivor, merged), new FakeCustomerMergeStore(), new FakeCustomerProvisioning(), new PassThroughPhones());

        Assert.Equal(new ShopifyCustomerRef(survivor.Id.Value, false, true), await directory.FindByIdAsync(TenantId, merged.Id.Value, Ct));
    }

    // Desvío F9: la búsqueda por documento ya no ve al fusionado (F8), así que sin dueño vivo se pregunta a la fusión.
    [Fact]
    public async Task FindByDocumentOnlyAMergedHasReturnsTheSurvivor()
    {
        var (survivor, merged) = MergedPair();
        var mergeStore = new FakeCustomerMergeStore { Survivors = { [(IdentificationType.Cc, "1020304050")] = survivor.Id.Value } };
        var directory = new ShopifyCustomerDirectory(
            new FakeCustomerRepository(survivor, merged), mergeStore, new FakeCustomerProvisioning(), new PassThroughPhones());

        Assert.Equal(new ShopifyCustomerRef(survivor.Id.Value, false, true), await directory.FindByDocumentAsync(TenantId, "CC", "1020304050", Ct));
        Assert.Null(await directory.FindByDocumentAsync(TenantId, "CC", "9999999999", Ct));
    }

    // El fusionado conserva su teléfono y su correo; el adaptador devuelve al que quedó (id y estado del que quedó).
    [Fact]
    public async Task FindByPhoneAndEmailOfAMergedReturnTheSurvivor()
    {
        var (survivor, merged) = MergedPair();
        var directory = new ShopifyCustomerDirectory(
            new FakeCustomerRepository(survivor, merged), new FakeCustomerMergeStore(), new FakeCustomerProvisioning(), new PassThroughPhones());
        var expected = new ShopifyCustomerRef(survivor.Id.Value, false, true);

        Assert.Equal(expected, await directory.FindByPhoneAsync(TenantId, "+573005556677", "CO", Ct));
        Assert.Equal(expected, await directory.FindByEmailAsync(TenantId, "laura.vieja@verde.co", Ct));
    }

    // Dos incompletos activos con teléfono y correo distintos; el segundo queda fusionado en el primero con el plan del dominio.
    private static (Customer Survivor, Customer Merged) MergedPair()
    {
        var survivor = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), TenantId, "Laura", "+573001234567", "laura@verde.co", "CO", Now, new PassThroughPhones());
        var merged = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), TenantId, "Laura P.", "+573005556677", "laura.vieja@verde.co", "CO", Now, new PassThroughPhones());
        CustomerMergePlan.Create(survivor, [merged]).MarkMerged(Now);
        return (survivor, merged);
    }

    [Fact]
    public async Task CustomerDirectoryCreateCompleteThatCannotCompleteThrowsItsOwnException()
    {
        var provisioning = new FakeCustomerProvisioning
        {
            OnComplete = () => throw new ValidationException([new ValidationFailure("Email", "Requerido.")])
        };
        var directory = new ShopifyCustomerDirectory(new FakeCustomerRepository(), new FakeCustomerMergeStore(), provisioning, new PassThroughPhones());

        var error = await Assert.ThrowsAsync<ShopifyCustomerNotCompletableException>(
            () => directory.CreateCompleteAsync(TenantId, CompleteCustomer(), Ct));

        Assert.Equal("validation.failed", error.Reason);

        provisioning.OnComplete = () => throw new CustomersDomainException("customers.customer.city_not_found", "No.");
        Assert.Equal(
            "customers.customer.city_not_found",
            (await Assert.ThrowsAsync<ShopifyCustomerNotCompletableException>(
                () => directory.CreateCompleteAsync(TenantId, CompleteCustomer(), Ct))).Reason);
    }

    [Fact]
    public async Task CustomerDirectoryCreatesCompleteAndIncomplete()
    {
        var provisioning = new FakeCustomerProvisioning();
        var directory = new ShopifyCustomerDirectory(new FakeCustomerRepository(), new FakeCustomerMergeStore(), provisioning, new PassThroughPhones());
        var data = CompleteCustomer();

        var complete = await directory.CreateCompleteAsync(TenantId, data, Ct);
        var incomplete = await directory.CreateIncompleteAsync(TenantId, null, "+573001234567", null, "CO", Ct);

        Assert.Equal(new ShopifyCustomerRef(provisioning.LastId, false, true, Created: true, Version: 1), incomplete);
        Assert.True(complete.IsComplete);
        var (tenantId, sent) = Assert.Single(provisioning.Completes);
        Assert.Equal(TenantId, tenantId);
        Assert.Equal(
            new CompleteCustomerData(
                data.Name, data.IdentificationType, data.IdentificationNumber, data.Phone, data.Email, data.Address,
                data.Country, data.CityId, data.CityName, data.ClassificationId),
            sent);
    }

    // Spec 2026-10-26 (desvío S7): la huella de un cliente que nació en la importación es su versión, y sólo si esta importación
    // lo creó: el dueño del documento que devuelve el alta (H8) no lo creó Shopify.
    [Fact]
    public async Task CustomerDirectoryReportsCreatedAndVersion()
    {
        var provisioning = new FakeCustomerProvisioning();
        var directory = new ShopifyCustomerDirectory(new FakeCustomerRepository(), new FakeCustomerMergeStore(), provisioning, new PassThroughPhones());

        var created = await directory.CreateCompleteAsync(TenantId, CompleteCustomer(), Ct);
        provisioning.Created = false;
        provisioning.Version = 4;
        var owner = await directory.CreateCompleteAsync(TenantId, CompleteCustomer(), Ct);

        Assert.Equal((true, 1L), (created.Created, created.Version));
        Assert.Equal((false, 4L), (owner.Created, owner.Version));
    }

    // ---- Conexión (§5.2) ----

    [Theory]
    [InlineData(ConnectionStatus.Active, true)]
    [InlineData(ConnectionStatus.Paused, false)]
    [InlineData(ConnectionStatus.NeedsAttention, false)]
    public async Task ConnectionDirectoryReturnsTheRouteInEveryStateLowercased(ConnectionStatus status, bool operational)
    {
        var world = new ConnectionWorld(status, shopifyEnabled: true);

        var found = await world.Directory.FindByShopDomainAsync("  Mi-Tienda.MyShopify.com ", Ct);

        Assert.NotNull(found);
        Assert.Equal(world.Connection.TenantId, found.TenantId);
        Assert.Equal(world.Connection.Id, found.ConnectionId);
        Assert.Equal("987654321", found.ShopId);
        Assert.Equal(ConnectionWorld.Domain, found.ShopDomain);
        Assert.Equal(operational, found.IsOperational);
        Assert.Equal(operational ? ConnectionWorld.Token : null, found.AccessToken);
    }

    // D-S4: el instante del enlace es la creación de la conexión, en los tres caminos del directorio.
    [Fact]
    public async Task ConnectionDirectoryCarriesTheLinkInstant()
    {
        var world = new ConnectionWorld(ConnectionStatus.Active, shopifyEnabled: true);
        var linkedAt = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        var byDomain = await world.Directory.FindByShopDomainAsync(ConnectionWorld.Domain, Ct);
        var byId = await world.Directory.FindAsync(world.Connection.TenantId, world.Connection.Id, Ct);
        var listed = Assert.Single(await world.Directory.ListOperationalAsync(Ct));

        Assert.Equal(linkedAt, world.Connection.CreatedAt);
        Assert.Equal([linkedAt, linkedAt, linkedAt], new[] { byDomain?.LinkedAt, byId?.LinkedAt, listed.LinkedAt });
    }

    [Fact]
    public async Task ConnectionDirectoryModuleOffIsNotOperational()
    {
        var world = new ConnectionWorld(ConnectionStatus.Active, shopifyEnabled: false);

        var found = await world.Directory.FindByShopDomainAsync(ConnectionWorld.Domain, Ct);

        Assert.NotNull(found);
        Assert.False(found.IsOperational);
        Assert.Null(found.AccessToken);
        Assert.Empty(await world.Directory.ListOperationalAsync(Ct));
    }

    [Fact]
    public async Task ConnectionDirectoryUnknownShopIsNull()
    {
        var world = new ConnectionWorld(ConnectionStatus.Active, shopifyEnabled: true);

        Assert.Null(await world.Directory.FindByShopDomainAsync("otra.myshopify.com", Ct));
        Assert.Null(await world.Directory.FindByShopDomainAsync("   ", Ct));
    }

    [Theory]
    [InlineData(ConnectionStatus.Active, 1)]
    [InlineData(ConnectionStatus.Paused, 0)]
    public async Task ConnectionDirectoryListsOnlyTheOperationalOnes(ConnectionStatus status, int expected)
    {
        var world = new ConnectionWorld(status, shopifyEnabled: true);

        var listed = await world.Directory.ListOperationalAsync(Ct);

        Assert.Equal(expected, listed.Count);
        Assert.All(listed, connection =>
        {
            Assert.True(connection.IsOperational);
            Assert.Equal(ConnectionWorld.Domain, connection.ShopDomain);
            Assert.Equal(ConnectionWorld.Token, connection.AccessToken);
        });
    }

    [Fact]
    public async Task ConnectionHealthReportsUninstalledAndRejectedWithTheirCodes()
    {
        var reporter = new RecordingHealthReporter();
        var health = new ShopifyConnectionHealth(reporter);
        var connectionId = Guid.CreateVersion7();

        await health.ReportUninstalledAsync(TenantId, connectionId, Ct);
        await health.ReportRejectedAsync(TenantId, connectionId, Ct);

        Assert.Equal(
            [(TenantId, connectionId, "app_uninstalled"), (TenantId, connectionId, "credentials_rejected")],
            reporter.Reports);
    }

    // ---- Ciudad, asesor y cuenta de cobro (§6.2 y §6.2.1 paso 5) ----

    [Fact]
    public async Task GeographyLookupResolvesTheCityInsideItsDepartment()
    {
        var antioquia = Department.Create(DepartmentId.New(), "05", "Antioquia");
        var medellin = City.Create(CityId.New(), "05001", "Medellín", antioquia.Id);
        var lookup = new ShopifyGeographyLookup(new NamedDepartments(antioquia), new NamedCities(medellin));

        Assert.Equal(new ShopifyCityRef(antioquia.Id.Value, medellin.Id.Value), (await lookup.LookupCityAsync("Antioquia", "Medellín", Ct)).City);
        Assert.Null((await lookup.LookupCityAsync("Antioquia", "Bello", Ct)).City);
        Assert.Null((await lookup.LookupCityAsync("Cundinamarca", "Medellín", Ct)).City);
        Assert.Null((await lookup.LookupCityAsync(" ", "Medellín", Ct)).City);
    }

    // Spec 2026-10-26 D-C13 y D-C11: la ciudad dice si casó por un sinónimo, y el departamento viaja aunque la ciudad no case
    // (es el de la bandeja). Sin departamento, o sin texto de ciudad, no hay nada.
    [Fact]
    public async Task GeographyLookupReportsTheSynonymAndTheUnmatchedDepartment()
    {
        var valle = Department.Create(DepartmentId.New(), "76", "VALLE DEL CAUCA");
        var cali = City.Create(CityId.New(), "76001", "Cali", valle.Id);
        var synonymId = CitySynonymId.New();
        var cities = new NamedCities(cali) { Synonyms = { [(valle.Id, "CALLI")] = (cali, synonymId) } };
        var lookup = new ShopifyGeographyLookup(new NamedDepartments(valle), cities);

        Assert.Equal(
            new ShopifyCityLookup(valle.Id.Value, new ShopifyCityRef(valle.Id.Value, cali.Id.Value, synonymId.Value)),
            await lookup.LookupCityAsync("VALLE DEL CAUCA", " CALLI ", Ct));
        Assert.Equal(
            new ShopifyCityLookup(valle.Id.Value, new ShopifyCityRef(valle.Id.Value, cali.Id.Value)),
            await lookup.LookupCityAsync("VALLE DEL CAUCA", "Cali", Ct));
        Assert.Equal(new ShopifyCityLookup(valle.Id.Value, null), await lookup.LookupCityAsync("VALLE DEL CAUCA", "Pueblo Perdido", Ct));
        Assert.Equal(new ShopifyCityLookup(valle.Id.Value, null), await lookup.LookupCityAsync("VALLE DEL CAUCA", "  ", Ct));
        Assert.Equal(new ShopifyCityLookup(null, null), await lookup.LookupCityAsync("Narnia", "Cali", Ct));
    }

    // Spec 2026-10-26 §4.2: Shopify reporta con su origen y la fila del espejo como referencia; lo que vuelve es el resultado y,
    // si el texto ya tiene sinónimo, cuál.
    [Fact]
    public async Task UnresolvedCityReporterMapsToGeography()
    {
        var geography = new RecordingUnresolvedCityReporter();
        var reporter = new ShopifyUnresolvedCityReporter(geography);
        var (mirrorId, orderId, customerId, departmentId) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var report = new ShopifyUnresolvedCityReport(
            TenantId, mirrorId, orderId, customerId, departmentId, "CALLI", "Cra 5 # 10-20, CALLI, Valle del Cauca, CO", "VAC",
            PartyCityOnly: true, CustomerCityOnly: false, Now);

        var recorded = await reporter.ReportAsync(report, Ct);
        var synonymId = CitySynonymId.New();
        geography.Result = new UnresolvedCityReportResult(UnresolvedCityReportOutcome.SynonymExists, null, synonymId, CityId.New());
        var existing = await reporter.ReportAsync(report, Ct);
        geography.Result = new UnresolvedCityReportResult(UnresolvedCityReportOutcome.Ignored, null, null, null);
        var ignored = await reporter.ReportAsync(report, Ct);

        Assert.Equal(
            new UnresolvedCityReport(
                TenantId, "shopify", mirrorId, orderId, customerId, new DepartmentId(departmentId), "CALLI",
                "Cra 5 # 10-20, CALLI, Valle del Cauca, CO", "VAC", PartyCityOnly: true, CustomerCityOnly: false, Now),
            geography.Reports[0]);
        Assert.Equal(new ShopifyUnresolvedCityResult(ShopifyUnresolvedCityOutcome.Recorded, null), recorded);
        Assert.Equal(new ShopifyUnresolvedCityResult(ShopifyUnresolvedCityOutcome.SynonymExists, synonymId.Value), existing);
        Assert.Equal(new ShopifyUnresolvedCityResult(ShopifyUnresolvedCityOutcome.Ignored, null), ignored);
    }

    [Fact]
    public async Task MemberDirectoryOnlyAnActiveMembershipOfTheTenantCounts()
    {
        var tenant = new TenantId(TenantId);
        var active = Membership.CreateActive(MembershipId.New(), Guid.CreateVersion7(), tenant, ["advisor"], "signup", Now);
        var invited = Membership.Invite(
            MembershipId.New(), Guid.CreateVersion7(), tenant, "Ana Gómez", ["advisor"], "invitation",
            "token-1", "hash-1", Now.AddHours(-1), Membership.DefaultInvitationTimeToLive);
        var directory = new ShopifyMemberDirectory(
            new InMemoryMembershipRepository(active, invited),
            new StubUserDirectory(new Dictionary<Guid, string> { [active.UserId] = "asesor@tienda.co" }));

        Assert.True(await directory.IsActiveMemberAsync(TenantId, active.Id.Value, Ct));
        Assert.False(await directory.IsActiveMemberAsync(TenantId, invited.Id.Value, Ct));
        Assert.False(await directory.IsActiveMemberAsync(Guid.CreateVersion7(), active.Id.Value, Ct));
        Assert.Equal("asesor@tienda.co", await directory.NameAsync(TenantId, active.Id.Value, Ct));
        Assert.Equal("Ana Gómez", await directory.NameAsync(TenantId, invited.Id.Value, Ct));
        Assert.Null(await directory.NameAsync(Guid.CreateVersion7(), active.Id.Value, Ct));
    }

    [Fact]
    public async Task BillingAccountsMatchesTheCompanyAccountAndGivesItsCurrency()
    {
        var company = Company.Create(
            CompanyId.New(), TenantId, "Verde S.A.S.",
            [new CompanyBankAccount { BankName = "Bancolombia", AccountNumber = "CTA-1", Currency = "USD" }],
            "900.111.222-3", Guid.CreateVersion7(), CompanyContactInfo.Empty, Now);
        var accounts = new ShopifyBillingAccounts(new FakeCompanyRepository(company));

        Assert.Equal(
            new ShopifyBillingAccountRef(company.Id.Value, "Bancolombia", "CTA-1", "USD"),
            await accounts.FindAsync(TenantId, company.Id.Value, " bancolombia ", " CTA-1 ", Ct));
        Assert.Null(await accounts.FindAsync(TenantId, company.Id.Value, "Bancolombia", "CTA-2", Ct));
        Assert.Null(await accounts.FindAsync(Guid.CreateVersion7(), company.Id.Value, "Bancolombia", "CTA-1", Ct));
    }

    // ---- Corrección de ciudades por sinónimo (spec 2026-10-26 §4.4 y §4.6) ----

    // Cada resultado de Quotations llega con su nombre, y la huella sólo con un éxito, como la guardó Quotations. Un pedido que no
    // es de una plataforma (order.order.source_invalid) es, para Shopify, un pedido que no encuentra: reintentar no lo arregla.
    [Fact]
    public async Task CityCorrectionWriterMapsTheOrderOutcomes()
    {
        var (departmentId, cityId, newCityId, orderId) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var persisted = new ExternalShippingParty("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", departmentId, cityId);
        var fingerprint = new ShopifyShippingFingerprint("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", departmentId, cityId);
        var cases = new (ExternalShippingOutcome Outcome, ShopifyCityWriteResult Expected)[]
        {
            (new(ExternalShippingOutcomeKind.Applied, persisted, null), new(ShopifyCityWriteKind.Applied, fingerprint, null, null)),
            (new(ExternalShippingOutcomeKind.AlreadyApplied, persisted, null), new(ShopifyCityWriteKind.AlreadyApplied, fingerprint, null, null)),
            (new(ExternalShippingOutcomeKind.NotFound, null, null), new(ShopifyCityWriteKind.NotFound, null, null, null)),
            (new(ExternalShippingOutcomeKind.NotExternal, null, null), new(ShopifyCityWriteKind.NotFound, null, null, "order.order.source_invalid")),
            (new(ExternalShippingOutcomeKind.NotPending, persisted, OrderStatus.Approved), new(ShopifyCityWriteKind.NotPending, null, null, "Approved")),
            (new(ExternalShippingOutcomeKind.StorePickup, null, null), new(ShopifyCityWriteKind.StorePickup, null, null, null)),
            (new(ExternalShippingOutcomeKind.ShippingPresent, persisted, null), new(ShopifyCityWriteKind.ShippingPresent, null, null, null)),
            (new(ExternalShippingOutcomeKind.ShippingChanged, persisted, null), new(ShopifyCityWriteKind.ShippingChanged, null, null, null)),
            (new(ExternalShippingOutcomeKind.PartyIncomplete, null, null), new(ShopifyCityWriteKind.PartyIncomplete, null, null, null)),
        };

        foreach (var (outcome, expected) in cases)
        {
            var attach = new FakeExternalOrderWriter { OnAttach = _ => outcome };
            Assert.Equal(
                expected,
                await CityCorrectionWriter(orders: attach).AttachShippingAsync(
                    TenantId, orderId, new ShopifyPartyDraft("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", departmentId, cityId),
                    "Dirección de envío de Shopify: ", Ct));
            var correct = new FakeExternalOrderWriter { OnCorrect = _ => outcome };
            Assert.Equal(
                expected,
                await CityCorrectionWriter(orders: correct).CorrectShippingCityAsync(TenantId, orderId, fingerprint, departmentId, newCityId, Ct));
        }

        // Lo que se le manda a Quotations, tal cual.
        var writer = new FakeExternalOrderWriter();
        var adapter = CityCorrectionWriter(orders: writer);
        await adapter.AttachShippingAsync(
            TenantId, orderId, new ShopifyPartyDraft("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", departmentId, cityId),
            "Dirección de envío de Shopify: ", Ct);
        var corrected = await adapter.CorrectShippingCityAsync(TenantId, orderId, fingerprint, departmentId, newCityId, Ct);
        Assert.Equal((TenantId, orderId, persisted, "Dirección de envío de Shopify: "), Assert.Single(writer.Attached));
        Assert.Equal((TenantId, orderId, persisted, departmentId, newCityId), Assert.Single(writer.Corrected));
        Assert.Equal(fingerprint with { CityId = newCityId }, corrected.Party);
    }

    // Customers devuelve la versión en que quedó el cliente (la huella, desvío S7) o el motivo por el que no lo tocó.
    [Fact]
    public async Task CityCorrectionWriterMapsTheCustomerOutcomes()
    {
        var (customerId, previousCityId, cityId) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var cases = new (ExternalCustomerCityOutcome Outcome, ShopifyCityWriteResult Expected)[]
        {
            (new(ExternalCustomerCityOutcomeKind.Applied, 2, null, cityId), new(ShopifyCityWriteKind.Applied, null, 2, null, cityId)),
            // La ciudad que quedó guardada, aunque no sea la que se pidió (un reintento después de que el sinónimo cambió).
            (new(ExternalCustomerCityOutcomeKind.AlreadyApplied, 2, null, previousCityId), new(ShopifyCityWriteKind.AlreadyApplied, null, 2, null, previousCityId)),
            (new(ExternalCustomerCityOutcomeKind.NotFound, null, null), new(ShopifyCityWriteKind.NotFound, null, null, null)),
            (new(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Edited), new(ShopifyCityWriteKind.NotEligible, null, null, "edited")),
            (new(ExternalCustomerCityOutcomeKind.NotCompletable, null, "validation.failed"), new(ShopifyCityWriteKind.NotCompletable, null, null, "validation.failed")),
        };

        foreach (var (outcome, expected) in cases)
        {
            var provisioning = new FakeCustomerProvisioning { OnExternalCity = () => outcome };
            var adapter = CityCorrectionWriter(provisioning: provisioning);
            Assert.Equal(expected, await adapter.CompleteCustomerAsync(TenantId, customerId, 1, CompleteCustomer(), [], Ct));
            Assert.Equal(expected, await adapter.CorrectCustomerCityAsync(TenantId, customerId, 2, previousCityId, cityId, [], Ct));
        }

        var sent = new FakeCustomerProvisioning();
        var writer = CityCorrectionWriter(provisioning: sent);
        var data = CompleteCustomer();
        // Las ciudades que el trabajo pidió en intentos anteriores viajan tal cual: con ellas Customers reconoce su propio reintento.
        Guid[] requested = [previousCityId, cityId];
        await writer.CompleteCustomerAsync(TenantId, customerId, 1, data, requested, Ct);
        await writer.CorrectCustomerCityAsync(TenantId, customerId, 2, previousCityId, cityId, requested, Ct);
        var completed = Assert.Single(sent.ExternalCompletes);
        Assert.Equal(
            (TenantId, customerId, 1L, new CompleteCustomerData(
                data.Name, data.IdentificationType, data.IdentificationNumber, data.Phone, data.Email, data.Address, data.Country,
                data.CityId, data.CityName, data.ClassificationId)),
            (completed.TenantId, completed.CustomerId, completed.ExpectedVersion, completed.Data));
        Assert.Equal(requested, completed.Requested);
        var corrected = Assert.Single(sent.CityCorrections);
        Assert.Equal(
            (TenantId, customerId, 2L, previousCityId, cityId),
            (corrected.TenantId, corrected.CustomerId, corrected.ExpectedVersion, corrected.PreviousCityId, corrected.CityId));
        Assert.Equal(requested, corrected.Requested);
    }

    // El trabajo tiene que saber que el sinónimo se borró (synonym_deleted), así que el adaptador no los filtra; uno que no
    // existe es null. Cada lectura va en su scope: la segunda de un mismo trabajo ve la base, no la primera.
    [Fact]
    public async Task CitySynonymLookupSeesDeletedSynonyms()
    {
        var (valle, cali) = (new DepartmentId(Guid.CreateVersion7()), new CityId(Guid.CreateVersion7()));
        var live = CitySynonym.Create(CitySynonymId.New(), valle, "Calli", cali, Guid.CreateVersion7(), Now);
        var deleted = CitySynonym.Create(CitySynonymId.New(), valle, "Kali", cali, Guid.CreateVersion7(), Now);
        deleted.Delete(Guid.CreateVersion7(), Now);
        var scopes = 0;
        var services = new ServiceCollection();
        services.AddScoped<ICitySynonymRepository>(_ =>
        {
            scopes++;
            return new FakeCitySynonymRepository(live, deleted);
        });
        var lookup = new ShopifyCitySynonymLookup(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(new ShopifyCitySynonymRef(live.Id.Value, valle.Value, cali.Value, false), await lookup.FindAsync(live.Id.Value, Ct));
        Assert.Equal(new ShopifyCitySynonymRef(deleted.Id.Value, valle.Value, cali.Value, true), await lookup.FindAsync(deleted.Id.Value, Ct));
        Assert.Null(await lookup.FindAsync(Guid.CreateVersion7(), Ct));
        Assert.Equal(3, scopes);
    }

    // ---- Armado ----

    private static ShopifyCityCorrectionWriter CityCorrectionWriter(
        FakeExternalOrderWriter? orders = null, FakeCustomerProvisioning? provisioning = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IExternalOrderWriter>(orders ?? new FakeExternalOrderWriter());
        services.AddSingleton<ICustomerProvisioning>(provisioning ?? new FakeCustomerProvisioning());
        return new ShopifyCityCorrectionWriter(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private static ShopifyShippingLookup ShippingLookup(
        FakeCarrierRepository carriers,
        FakeQuotationSettingsStore? settings = null,
        FakeTaxRateRepository? taxRates = null) =>
        new(carriers, settings ?? new FakeQuotationSettingsStore(null), taxRates ?? new FakeTaxRateRepository());

    private static ShopifyOrderWriter OrderWriter(FakeExternalOrderWriter writer)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IExternalOrderWriter>(writer);
        return new ShopifyOrderWriter(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private static ShopifyOrderDraft Draft() =>
        new(
            TenantId,
            "#1001",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Bogus, Contra entrega",
            "Shopify #1001",
            null,
            new ShopifyBillingAccountRef(Guid.CreateVersion7(), "Bancolombia", "CTA-1", "COP"),
            [new ShopifyOrderLineDraft(Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 10.5m, 19)],
            "pending",
            Now,
            null,
            "123:456",
            null);

    private static ShopifyCompleteCustomer CompleteCustomer() =>
        new("Laura Gómez", "CC", "1020304050", "+573001234567", "laura@verde.co", "Cra 1 # 2-3", "CO",
            Guid.CreateVersion7(), "Medellín", Guid.CreateVersion7());

    private static Product NewProduct(string code, TaxRateId? taxRateId) =>
        Product.Create(
            ProductId.New(), TenantId, $"Producto {code}",
            new ProductDetails { TaxRateId = taxRateId },
            new ProductVariantInput { Code = code, Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 11_900m } } }, [],
            Now);

    // ---- Conexiones del tenant (§7 GET /stores) ----

    // Una conexión sin dominio guardado da shopDomain null, igual que ShopifyStoreDto; nunca "".
    [Fact]
    public async Task TenantConnectionsWithoutShopDomainGiveNull()
    {
        var withDomain = new ConnectionWorld(ConnectionStatus.Active, shopifyEnabled: true).Connection;
        var withoutDomain = IntegrationConnection.Create(
            IntegrationProviders.Shopify,
            withDomain.TenantId,
            "Sin dominio",
            new Dictionary<string, string>(StringComparer.Ordinal) { [ShopifyFieldKeys.ShopId] = "1" },
            new Dictionary<string, string>(StringComparer.Ordinal),
            (connectionId, fieldKey, plaintext) => new ProtectedSecret("k1", System.Text.Encoding.UTF8.GetBytes(plaintext)),
            Guid.CreateVersion7(),
            Now);
        var adapter = new ShopifyTenantConnections(new FakeConnectionRepository(withDomain, withoutDomain));

        var listed = await adapter.ListAsync(withDomain.TenantId, Ct);

        Assert.Equal(
            [(withDomain.Id, (string?)ConnectionWorld.Domain, "Active"), (withoutDomain.Id, (string?)null, "Active")],
            listed.Select(connection => (connection.ConnectionId, (string?)connection.ShopDomain, connection.Status)));
    }
}
