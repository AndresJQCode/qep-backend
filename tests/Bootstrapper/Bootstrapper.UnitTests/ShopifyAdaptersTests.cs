using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Domain;
using Modules.Companies.Domain;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Geography.Domain;
using Modules.Integrations.Domain;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Shopify.Application;
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

    // ---- Productos (§6.2.2) ----

    [Fact]
    public async Task ProductLookupReadsTaxPercentageByRateId()
    {
        var rate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA 19", 19, Now);
        var taxed = NewProduct("SH-400", rate.Id);
        var untaxed = NewProduct("SH-200", null);
        untaxed.Deactivate(Now);
        var lookup = new ShopifyProductLookup(new FakeProductRepository(taxed, untaxed), new FakeTaxRateRepository(rate));

        var found = await lookup.FindByCodesAsync(TenantId, ["SH-400", "SH-200", "NO-EXISTE"], Ct);

        Assert.Equal(new ShopifyProductRef(taxed.Id.Value, "SH-400", true, 19), found["SH-400"]);
        Assert.Equal(new ShopifyProductRef(untaxed.Id.Value, "SH-200", false, 0), found["SH-200"]);
        Assert.False(found.ContainsKey("NO-EXISTE"));
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
        // El cliente del pedido que ya existía, no el del borrador (revisión de la tarea 13, minor 4).
        Assert.NotEqual(draft.ClientId, result.ClientId);
        Assert.Equal(existingClient, result.ClientId);
        Assert.Equal("PED-7", result.OrderNumber);
        Assert.Equal(134_000m, result.Total);
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
        Assert.Equal([new ExternalOrderLine(draft.Lines[0].ProductId, 2m, 11_900m, 10.5m, 19)], sent.Lines);
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

    // Arrastre de la tarea 2: IExternalOrderWriter descarta TODO el tracker de Quotations ante la carrera de la clave
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
        var directory = new ShopifyCustomerDirectory(customers, new FakeCustomerProvisioning(), new PassThroughPhones());

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
        var directory = new ShopifyCustomerDirectory(customers, new FakeCustomerProvisioning(), new PassThroughPhones());
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
        var directory = new ShopifyCustomerDirectory(customers, new FakeCustomerProvisioning(), phones);

        Assert.Equal(customer.Id.Value, (await directory.FindByPhoneAsync(TenantId, " 300 123 4567 ", null, Ct))?.Id);
        Assert.Equal(customer.Id.Value, (await directory.FindByPhoneAsync(TenantId, "300 123 4567", "co", Ct))?.Id);
        var calls = customers.Calls;
        Assert.Null(await directory.FindByPhoneAsync(TenantId, "no es un teléfono", "CO", Ct));
        Assert.Equal(calls, customers.Calls);
    }

    [Fact]
    public async Task CustomerDirectoryCreateCompleteThatCannotCompleteThrowsItsOwnException()
    {
        var provisioning = new FakeCustomerProvisioning
        {
            OnComplete = () => throw new ValidationException([new ValidationFailure("Email", "Requerido.")])
        };
        var directory = new ShopifyCustomerDirectory(new FakeCustomerRepository(), provisioning, new PassThroughPhones());

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
        var directory = new ShopifyCustomerDirectory(new FakeCustomerRepository(), provisioning, new PassThroughPhones());
        var data = CompleteCustomer();

        var complete = await directory.CreateCompleteAsync(TenantId, data, Ct);
        var incomplete = await directory.CreateIncompleteAsync(TenantId, null, "+573001234567", null, "CO", Ct);

        Assert.Equal(new ShopifyCustomerRef(provisioning.LastId, false, true), incomplete);
        Assert.True(complete.IsComplete);
        var (tenantId, sent) = Assert.Single(provisioning.Completes);
        Assert.Equal(TenantId, tenantId);
        Assert.Equal(
            new CompleteCustomerData(
                data.Name, data.IdentificationType, data.IdentificationNumber, data.Phone, data.Email, data.Address,
                data.Country, data.CityId, data.CityName, data.ClassificationId),
            sent);
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

    // D-S4 (ruling S25): el instante del enlace es la creación de la conexión, en los tres caminos del directorio.
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

        Assert.Equal(new ShopifyCityRef(antioquia.Id.Value, medellin.Id.Value), await lookup.FindCityAsync("Antioquia", "Medellín", Ct));
        Assert.Null(await lookup.FindCityAsync("Antioquia", "Bello", Ct));
        Assert.Null(await lookup.FindCityAsync("Cundinamarca", "Medellín", Ct));
        Assert.Null(await lookup.FindCityAsync(" ", "Medellín", Ct));
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

    // ---- Armado ----

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
            [new ShopifyOrderLineDraft(Guid.CreateVersion7(), 2m, 11_900m, 10.5m, 19)],
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
            ProductId.New(), TenantId, $"Producto {code}", code,
            new ProductDetails { TaxRateId = taxRateId },
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 11_900m } },
            Now);

    // ---- Conexiones del tenant (§7 GET /stores) ----

    // Minor 6 de la revisión: una conexión sin dominio guardado da shopDomain null, igual que ShopifyStoreDto; nunca "".
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
