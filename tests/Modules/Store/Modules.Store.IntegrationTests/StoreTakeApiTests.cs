using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Store.Domain;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// «Tomar» de punta a punta: cliente, libreta, pedido y marca de la solicitud, con el lease como único candado. Necesitan Docker; se
/// corren una vez en la tanda final.
/// </summary>
public sealed class StoreTakeApiTests
{
    private static readonly string[] ColombiaAndSpain = ["CO", "ES"];

    private static readonly string[] ColombiaSpainAndUnitedStates = ["CO", "ES", "US"];

    private static readonly object NewCustomerBody = new { newCustomer = new { classificationId = (Guid?)null } };

    private static object ExistingBody(Guid customerId) => new { customerId };

    private static JsonNode Party(JsonNode order, string role) =>
        order["order"]!["parties"]!.AsArray()
            .Single(party => string.Equals((string)party!["role"]!, role, StringComparison.OrdinalIgnoreCase))!;

    // El cliente de la API trae la libreta con la dirección de facturación del formulario ("Calle 1 # 2-3", teléfono 3001234567).
    private static Task<Guid> BillingTwinAsync(StoreInboxRig rig, bool withRetention = false) =>
        rig.CreateCompleteCustomerAsync(
            "Ana Pérez", "123456", "3001234567", "ana@test.co", "Calle 1 # 2-3", withRetention: withRetention);

    [Fact]
    public async Task TakingWithAnExistingCustomerCreatesAStoreOrder()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var customer = await rig.CreateCompleteCustomerAsync("Cliente Viejo", "777", "3105551111");
        var request = await rig.SubmitAsync();

        var taken = await rig.TakeOkAsync(request, ExistingBody(customer));

        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        Assert.Equal("Store", (string)order["order"]!["source"]!);
        Assert.Equal(rig.CallerMembershipId, (Guid)order["order"]!["advisorId"]!);
        Assert.False((bool)order["order"]!["isStorePickup"]!);
        // El pedido de la tienda ya no lleva el número de la solicitud como referencia.
        Assert.Null(order["order"]!["externalReference"]);
        Assert.Equal(customer, (Guid)order["order"]!["clientId"]!);
        Assert.False((bool)taken["customerCreated"]!);
        Assert.False((bool)taken["customerCompleted"]!);
        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.Converted, state.Status);
        Assert.Equal((Guid)taken["orderId"]!, state.OrderId);
        Assert.Equal(1, await CountAuditEntriesAsync(rig.Factory, "store.order_request.converted", request.ToString()));
    }

    [Fact]
    public async Task TakingWithANewCustomerCreatesACompleteCustomerFromTheBilling()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        Assert.True((bool)taken["customerCreated"]!);
        var customer = await rig.CustomerAsync((Guid)taken["customerId"]!);
        Assert.Equal("Complete", customer.Completeness);
        Assert.False(string.IsNullOrEmpty(customer.Cuc));
        Assert.Equal("Ana Pérez", customer.Name);
        Assert.Equal("123456", customer.IdentificationNumber);
        Assert.Equal("+573001234567", customer.PhoneE164);
        Assert.Equal("ana@test.co", customer.Email);
        Assert.Equal("CO", customer.Country);
        Assert.Equal(rig.City, customer.CityId);
        Assert.Null(customer.ClassificationId);
        Assert.Equal("Reused", (string)taken["addresses"]!["billing"]!);
    }

    [Fact]
    public async Task TheNewCustomerGetsTheClassificationTheDialogChose()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        // Crear la clasificación pide su propio permiso (ClientClassificationEndpoints.cs:32-33), que quien atiende la bandeja no tiene.
        var admin = CreateClient(rig.Factory, SubjectId, TenantId, "customers.classification.manage");
        var created = await admin.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/customers/classifications",
            new { name = "Mayorista", prefix = "MAY" },
            TestContext.Current.CancellationToken);
        var classification = (Guid)(await PublicStoreFixture.JsonAsync(created))["id"]!;
        var request = await rig.SubmitAsync();

        var taken = await rig.TakeOkAsync(request, new { newCustomer = new { classificationId = classification } });

        var customer = await rig.CustomerAsync((Guid)taken["customerId"]!);
        Assert.Equal(classification, customer.ClassificationId);
        Assert.StartsWith("MAY", customer.Cuc, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TakingAnExistingCustomerNeedsExactlyOneOfTheTwoChoices()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();

        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, new { }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, new { customerId = Guid.CreateVersion7(), newCustomer = new { classificationId = (Guid?)null } }),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");
        Assert.Equal(StoreOrderRequestStatus.New, (await rig.StateAsync(request)).Status);
    }

    [Fact]
    public async Task TakingWithAnExistingCustomerNeverTouchesItsData()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var customerId = await rig.CreateCompleteCustomerAsync(
            "Cliente Viejo", "777", "3105551111", "viejo@test.co", "Avenida 5 # 6-7");
        var before = await rig.CustomerAsync(customerId);
        var request = await rig.SubmitAsync();

        await rig.TakeOkAsync(request, ExistingBody(customerId));

        // Todo el bloque de facturación es otro, y aun así la ficha queda igual. Sólo sube la versión: la libreta es parte del
        // agregado y agregar la dirección de facturación lo toca (Customer.cs:713, AddAddress → Touch).
        var after = await rig.CustomerAsync(customerId);
        Assert.Equal(before with { Version = after.Version }, after);
        Assert.Equal(before.Version + 1, after.Version);
    }

    [Fact]
    public async Task AnAddressAlreadyInTheBookIsReused()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var customer = await BillingTwinAsync(rig);
        var request = await rig.SubmitAsync();

        var taken = await rig.TakeOkAsync(request, ExistingBody(customer));

        Assert.Equal("Reused", (string)taken["addresses"]!["billing"]!);
        Assert.Null(taken["addresses"]!["shipping"]);
        Assert.Single(await rig.AddressesAsync(customer));
    }

    [Fact]
    public async Task ADifferentShippingAddressIsAdded()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var customer = await BillingTwinAsync(rig);
        var request = await rig.SubmitAsync(shipping: _ => { });

        var taken = await rig.TakeOkAsync(request, ExistingBody(customer));

        Assert.Equal("Reused", (string)taken["addresses"]!["billing"]!);
        Assert.Equal("Added", (string)taken["addresses"]!["shipping"]!);
        var book = await rig.AddressesAsync(customer);
        Assert.Equal(2, book.Count);
        var added = book.Single(entry => entry.Address == "Carrera 9 # 8-7");
        Assert.False(added.IsPrincipal);
        Assert.Equal("Luis Gómez", added.Name);
        Assert.Equal("+573007654321", added.Phone);
    }

    [Fact]
    public async Task ARetakeDoesNotAddTheAddressTwice()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var customer = await BillingTwinAsync(rig);
        var request = await rig.SubmitAsync(shipping: _ => { });
        // El pedido falla después de agregar la dirección de envío (paso 8).
        await rig.DeactivateVariantAsync();
        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, ExistingBody(customer)), HttpStatusCode.UnprocessableEntity, "store.order_request.line_unavailable");
        Assert.Equal(2, (await rig.AddressesAsync(customer)).Count);
        await rig.ExecuteAsync("UPDATE catalog.product_variants SET is_active = true WHERE id = @id", ("id", rig.Variant));

        var retaken = await rig.TakeAsync(request, ExistingBody(customer));

        Assert.Equal(HttpStatusCode.OK, retaken.StatusCode);
        Assert.Equal(2, (await rig.AddressesAsync(customer)).Count);
    }

    [Fact]
    public async Task AnIncompleteCustomerIsCompletedOnlyInItsBlankFields()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        // Como la deja el POS: nombre y documento, sin correo, dirección ni ciudad.
        var customerId = await rig.SeedIncompleteAsync("Ana P.", phone: "3001234567", phoneE164: "+573001234567", documentNumber: "123456");
        var request = await rig.SubmitAsync();

        var taken = await rig.TakeOkAsync(request, ExistingBody(customerId));

        Assert.True((bool)taken["customerCompleted"]!);
        var customer = await rig.CustomerAsync(customerId);
        Assert.Equal("Complete", customer.Completeness);
        Assert.Equal("Ana P.", customer.Name);
        Assert.Equal("123456", customer.IdentificationNumber);
        Assert.Equal("ana@test.co", customer.Email);
        Assert.Equal("Calle 1 # 2-3", customer.Address);
        Assert.Equal(rig.City, customer.CityId);
        Assert.False(string.IsNullOrEmpty(customer.Cuc));
    }

    [Fact]
    public async Task CompletingNeverOverwritesAnExistingValue()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        // Un cliente de WhatsApp: nombre y teléfono, nada más.
        var customerId = await rig.SeedIncompleteAsync("Pedro WhatsApp", phone: "+57 310 111 2233", phoneE164: "+573101112233");
        var request = await rig.SubmitAsync();

        await rig.TakeOkAsync(request, ExistingBody(customerId));

        var customer = await rig.CustomerAsync(customerId);
        Assert.Equal("Pedro WhatsApp", customer.Name);
        Assert.Equal("+57 310 111 2233", customer.Phone);
        Assert.Equal("123456", customer.IdentificationNumber);
        Assert.Equal("ana@test.co", customer.Email);
        Assert.Equal("Calle 1 # 2-3", customer.Address);
        Assert.Equal("Complete", customer.Completeness);
    }

    [Fact]
    public async Task AnIncompleteCustomerWithoutDocumentTakesTheRequestCountryWithItsDocument()
    {
        await using var rig = await StoreInboxRig.OpenAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);
        var customerId = await rig.SeedIncompleteAsync("Pedro WhatsApp", phone: "+57 310 111 2233", phoneE164: "+573101112233", country: "CO");
        var request = await rig.SubmitAsync(billing =>
        {
            billing["identificationType"] = "PASAPORTE"; billing["identificationNumber"] = "X1234567";
            billing["country"] = "ES"; billing["departmentId"] = null; billing["cityId"] = null; billing["cityName"] = "Madrid";
            billing["phone"] = "+34600111222"; billing["address"] = "Calle Mayor 1";
        });

        await rig.TakeOkAsync(request, ExistingBody(customerId));

        var customer = await rig.CustomerAsync(customerId);
        Assert.Equal(("Complete", "Pasaporte", "ES", "Madrid"), (customer.Completeness, customer.IdentificationType, customer.Country, customer.CityName));
        Assert.Equal("+573101112233", customer.Phone);
    }

    [Fact]
    public async Task AddressesAreAddedAfterCompletion()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var customerId = await rig.SeedIncompleteAsync("Pedro WhatsApp", phone: "3101112233", phoneE164: "+573101112233");
        var request = await rig.SubmitAsync(shipping: _ => { });

        var taken = await rig.TakeOkAsync(request, ExistingBody(customerId));

        Assert.True((bool)taken["customerCompleted"]!);
        // Completar siembra la principal con la calle y la ciudad de facturación pero con el teléfono del cliente, que gana por no estar
        // en blanco (CustomerProvisioning.cs:542-548). El teléfono cuenta para la libreta, así que la de facturación es otra entrada.
        Assert.Equal("Added", (string)taken["addresses"]!["billing"]!);
        Assert.Equal("Added", (string)taken["addresses"]!["shipping"]!);
        var addresses = await rig.AddressesAsync(customerId);
        Assert.Equal(3, addresses.Count);
        Assert.Single(addresses, address => address.IsPrincipal && address.Phone == "3101112233");
    }

    [Fact]
    public async Task AMergedOrInactiveCustomerIsStillRejected()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var inactive = await rig.CreateCompleteCustomerAsync("Inactivo", "401", "3105550001");
        var merged = await rig.CreateCompleteCustomerAsync("Fusionado", "402", "3105550002");
        var survivor = await rig.CreateCompleteCustomerAsync("Sobreviviente", "403", "3105550003");
        await DeactivateCustomerAsync(rig.Factory, inactive);
        await rig.MergeAsync(merged, survivor);
        var request = await rig.SubmitAsync();

        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, ExistingBody(inactive)), HttpStatusCode.UnprocessableEntity, "store.order_request.customer_not_found");
        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, ExistingBody(merged)), HttpStatusCode.UnprocessableEntity, "store.order_request.customer_not_found");
        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, ExistingBody(Guid.CreateVersion7())), HttpStatusCode.UnprocessableEntity, "store.order_request.customer_not_found");

        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.New, state.Status);
        Assert.False(state.IsBeingTaken(rig.Clock.UtcNow));
    }

    [Fact]
    public async Task ACompletionWhoseDocumentIsTakenIs409()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        await rig.CreateCompleteCustomerAsync("Dueño del documento", "123456", "3105559999");
        var whatsApp = await rig.SeedIncompleteAsync("Pedro WhatsApp", phone: "3101112233", phoneE164: "+573101112233");
        var request = await rig.SubmitAsync();

        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, ExistingBody(whatsApp)),
            HttpStatusCode.Conflict,
            "store.order_request.customer_document_taken");

        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.New, state.Status);
        Assert.False(state.IsBeingTaken(rig.Clock.UtcNow));
        Assert.Equal("Incomplete", (await rig.CustomerAsync(whatsApp)).Completeness);
    }

    [Fact]
    public async Task AForeignBillingCreatesAForeignCustomerAndAddress()
    {
        await using var rig = await StoreInboxRig.OpenAsync(
            (_, body) => body["salesCountries"] = ColombiaAndSpain);
        var request = await rig.SubmitAsync(billing =>
        {
            billing["identificationType"] = "PASAPORTE";
            billing["country"] = "ES";
            billing["departmentId"] = null;
            billing["cityId"] = null;
            billing["cityName"] = "Madrid";
            billing["phone"] = "+34600111222";
            billing["address"] = "Calle Mayor 1";
        });

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        var customerId = (Guid)taken["customerId"]!;
        var customer = await rig.CustomerAsync(customerId);
        Assert.Equal("ES", customer.Country);
        Assert.Equal("Madrid", customer.CityName);
        Assert.Null(customer.CityId);
        // Un cliente de afuera nace sin libreta: la dirección es la que agrega «Tomar».
        Assert.Equal("Added", (string)taken["addresses"]!["billing"]!);
        var book = Assert.Single(await rig.AddressesAsync(customerId));
        Assert.Equal("ES", book.Country);
        Assert.Equal("Madrid", book.CityName);
        Assert.Null(book.CityId);
        Assert.True(book.IsPrincipal);
        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        Assert.Equal("ES", (string)Party(order, "Billing")["country"]!);
        Assert.Equal("Madrid", (string)Party(order, "Billing")["cityName"]!);
    }

    [Fact]
    public async Task TheOrderCarriesTheBillingAndShippingParties()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync(shipping: _ => { });

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        var billing = Party(order, "Billing");
        var shipping = Party(order, "Shipping");
        Assert.Equal("Ana Pérez", (string)billing["name"]!);
        Assert.Equal("123456", (string)billing["identificationNumber"]!);
        Assert.Equal("CC", (string)billing["identificationType"]!);
        Assert.Equal("Calle 1 # 2-3", (string)billing["address"]!);
        Assert.Equal("Luis Gómez", (string)shipping["name"]!);
        Assert.Equal("Carrera 9 # 8-7", (string)shipping["address"]!);
        Assert.Equal("3007654321", (string)shipping["phone"]!);
        // El bloque de envío no tiene correo: las dos partes llevan el de facturación.
        Assert.Equal("ana@test.co", (string)billing["email"]!);
        Assert.Equal("ana@test.co", (string)shipping["email"]!);
        Assert.False((bool)order["order"]!["isStorePickup"]!);
    }

    [Fact]
    public async Task WithTheSameDataTheShippingPartyRepeatsTheBilling()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        var billing = Party(order, "Billing");
        var shipping = Party(order, "Shipping");
        Assert.Equal((string)billing["name"]!, (string)shipping["name"]!);
        Assert.Equal((string)billing["address"]!, (string)shipping["address"]!);
        Assert.Equal((string)billing["phone"]!, (string)shipping["phone"]!);
        Assert.Equal((string)billing["email"]!, (string)shipping["email"]!);
        Assert.Equal((Guid)billing["cityId"]!, (Guid)shipping["cityId"]!);
    }

    [Fact]
    public async Task TheBillingTaxProfileIsTheCustomers()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var withRetention = await rig.CreateCompleteCustomerAsync("Retenedor", "888", "3105552222", withRetention: true);
        var request = await rig.SubmitAsync();

        var taken = await rig.TakeOkAsync(request, ExistingBody(withRetention));

        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        Assert.True((bool)order["order"]!["billingWithRetention"]!);
        Assert.False((bool)order["order"]!["billingVatSurplus"]!);
    }

    [Fact]
    public async Task TheTakenOrderTotalEqualsTheQuotedTotal()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        Assert.Equal(20_000m, (decimal)order["order"]!["total"]!);
        Assert.Null(taken["request"]!["totalDifference"]);
    }

    [Fact]
    public async Task APriceChangeAfterSubmissionShowsATotalDifference()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();
        await rig.SetPriceAsync(12_000m);

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        Assert.Equal(20_000m, (decimal)taken["request"]!["total"]!);
        Assert.Equal(4_000m, (decimal)taken["request"]!["totalDifference"]!);
        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        Assert.Equal(24_000m, (decimal)order["order"]!["total"]!);
    }

    [Fact]
    public async Task TwoConcurrentTakesCreateOneOrder()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var (userId, _) = await rig.SeedSecondCallerAsync("Segundo asesor");
        var second = rig.ClientFor(userId);
        var request = await rig.SubmitAsync();

        var responses = await Task.WhenAll(rig.TakeAsync(request, NewCustomerBody), rig.TakeAsync(request, NewCustomerBody, second));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await rig.CountAsync("quotations.orders"));
    }

    // Un doble clic del mismo asesor: la segunda toma no reentra el lease vivo, así que una sola corre (revisión de la tarea 12).
    [Fact]
    public async Task TwoConcurrentTakesByTheSameMemberCreateOneOrder()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();

        var responses = await Task.WhenAll(rig.TakeAsync(request, NewCustomerBody), rig.TakeAsync(request, NewCustomerBody));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await rig.CountAsync("quotations.orders"));
        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.Converted, state.Status);
        Assert.Equal(1, await rig.CountAsync("customers.customers"));
    }

    [Fact]
    public async Task ANewCustomerWhoseDocumentBelongsToAnInactiveCustomerIs422()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        // El mismo documento del bloque de facturación ("CC" 123456), en un cliente apagado.
        var inactive = await BillingTwinAsync(rig);
        await DeactivateCustomerAsync(rig.Factory, inactive);
        var request = await rig.SubmitAsync();

        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, NewCustomerBody), HttpStatusCode.UnprocessableEntity, "store.order_request.customer_not_found");

        Assert.Equal(0, await rig.CountAsync("quotations.orders"));
        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.New, state.Status);
        Assert.False(state.IsBeingTaken(rig.Clock.UtcNow));
    }

    [Fact]
    public async Task TheLoserOfATakeCreatesNoCustomer()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var (userId, _) = await rig.SeedSecondCallerAsync("Segundo asesor");
        var second = rig.ClientFor(userId);
        var request = await rig.SubmitAsync(shipping: _ => { });

        var responses = await Task.WhenAll(rig.TakeAsync(request, NewCustomerBody), rig.TakeAsync(request, NewCustomerBody, second));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await rig.CountAsync("customers.customers"));
        // La facturación sembrada con el cliente y el envío del ganador; el perdedor no agrega nada.
        Assert.Equal(2, await rig.CountAsync("customers.customer_addresses"));
    }

    [Fact]
    public async Task DiscardingDuringALiveLeaseIs409BeingTakenEvenForTheSameMember()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();
        await rig.ExecuteAsync(
            "UPDATE store.order_requests SET lease_member_id = @member, lease_until = @until WHERE id = @id",
            ("member", rig.CallerMembershipId),
            ("until", StoreInboxRig.Start.AddMinutes(2)),
            ("id", request));

        await StoreInboxRig.AssertCodeAsync(
            await rig.DiscardAsync(request, new { reason = "Spam" }), HttpStatusCode.Conflict, "store.order_request.being_taken");

        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.New, state.Status);
    }

    [Fact]
    public async Task DiscardingARequestWhoseOrderAlreadyExistsConvertsItInstead()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var customer = await BillingTwinAsync(rig);
        var request = await rig.SubmitAsync(shipping: _ => { });
        // «Tomar» creó el pedido y cayó antes de marcar la solicitud; no hay lease vivo.
        await CreateOrderBehindTheInboxAsync(rig, request, customer);

        await StoreInboxRig.AssertCodeAsync(
            await rig.DiscardAsync(request, new { reason = "Spam" }), HttpStatusCode.Conflict, "store.order_request.not_new");

        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.Converted, state.Status);
        Assert.NotNull(state.OrderId);
        Assert.Equal(customer, state.CustomerId);
        Assert.Equal(1, await rig.CountAsync("quotations.orders"));
    }

    // El pedido que «Tomar» alcanzó a crear antes de caer: existe en Pedidos, con la clave de la solicitud, y la solicitud sigue Nueva.
    private static async Task CreateOrderBehindTheInboxAsync(StoreInboxRig rig, Guid request, Guid customer)
    {
        await using var scope = rig.Factory.Services.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IStoreOrderGateway>();
        // La clave del pedido sale del clientRequestId del checkout, no del id de la solicitud.
        var clientRequestId = (await rig.StateAsync(request)).ClientRequestId;
        var party = new StoreOrderParty(
            "Ana Pérez", "3001234567", "ana@test.co", "Calle 1 # 2-3", "CO", rig.Department, rig.City, null, "CC", "123456");
        await gateway.CreateAsync(
            new Modules.Quotations.Application.StoreOrderDraft(
                StoreSettingsFixture.Tenant,
                clientRequestId,
                customer,
                rig.CallerMembershipId,
                true,
                new QuotationBillingAccountRequest(
                    rig.Fixture.Settings.Company.Id,
                    rig.Fixture.Settings.Company.BankName,
                    rig.Fixture.Settings.Company.AccountNumber,
                    "COP"),
                party,
                party with { IdentificationType = null, IdentificationNumber = null },
                false,
                false,
                [new StoreQuoteLine(rig.Variant, 2)],
                null),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARetakeAfterACrashMarksTheExistingOrder()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var customer = await BillingTwinAsync(rig);
        var request = await rig.SubmitAsync(shipping: _ => { });
        var (userId, _) = await rig.SeedSecondCallerAsync("Segundo asesor");

        // El primer asesor creó el pedido y el proceso cayó antes de marcar la solicitud: el lease venció.
        await CreateOrderBehindTheInboxAsync(rig, request, customer);

        await rig.ExecuteAsync(
            "UPDATE store.order_requests SET lease_member_id = @member, lease_until = @until WHERE id = @id",
            ("member", rig.CallerMembershipId),
            ("until", StoreInboxRig.Start.AddHours(-1)),
            ("id", request));
        var addressesBefore = await rig.AddressesAsync(customer);
        // El segundo tomador elige OTRO cliente: R5 marca con el del pedido que ya existe y no toca al elegido.
        var chosen = await rig.CreateCompleteCustomerAsync("Cliente Elegido", "888", "3105552222");
        var chosenBefore = await rig.CustomerAsync(chosen);
        var chosenAddressesBefore = await rig.AddressesAsync(chosen);

        var retaken = await rig.TakeOkAsync(request, ExistingBody(chosen), rig.ClientFor(userId));

        Assert.Equal(rig.CallerMembershipId, (Guid)retaken["request"]!["convertedBy"]!["memberId"]!);
        Assert.Equal(customer, (Guid)retaken["customerId"]!);
        Assert.Equal(1, await rig.CountAsync("quotations.orders"));
        var order = await rig.OrderAsync((Guid)retaken["orderId"]!);
        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.Converted, state.Status);
        Assert.Equal((Guid)order["order"]!["clientId"]!, state.CustomerId);
        Assert.Equal(customer, state.CustomerId);
        // Con el pedido ya creado no se toca ninguna libreta, y no hay resultado de direcciones que informar.
        Assert.Equal(addressesBefore, await rig.AddressesAsync(customer));
        Assert.Equal(chosenAddressesBefore, await rig.AddressesAsync(chosen));
        Assert.Equal(chosenBefore, await rig.CustomerAsync(chosen));
        Assert.Null(retaken["addresses"]!["billing"]);
        Assert.Null(retaken["addresses"]!["shipping"]);
    }

    [Fact]
    public async Task AFailedTakeReleasesTheLease()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();
        await rig.DeactivateVariantAsync();

        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, NewCustomerBody), HttpStatusCode.UnprocessableEntity, "store.order_request.line_unavailable");

        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.New, state.Status);
        Assert.False(state.IsBeingTaken(rig.Clock.UtcNow));
        Assert.False((bool)(await rig.GetJsonAsync($"/{request}"))["isBeingTaken"]!);
    }

    [Fact]
    public async Task RetailModeTakesAsRetail()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        Assert.True((bool)order["order"]!["isRetail"]!);
        Assert.Null(order["order"]!["globalScaleFloor"]);
    }

    [Fact]
    public async Task TakingAEurRequestUsesTheEurAccount()
    {
        await using var rig = await StoreInboxRig.OpenAsync(PublicStoreFixture.TwoCurrencies, eurPrice: true);
        var request = await rig.SubmitAsync(currency: "EUR");

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        Assert.Equal("EUR", (string)order["order"]!["currency"]!);
        Assert.Equal("B-EUR-1", (string)order["billingAccount"]!["accountNumber"]!);
    }

    [Fact]
    public async Task TakingARequestWhoseCurrencyWasRemovedIs422AndKeepsItNew()
    {
        await using var rig = await StoreInboxRig.OpenAsync(PublicStoreFixture.TwoCurrencies, eurPrice: true);
        var request = await rig.SubmitAsync(currency: "EUR");
        var settings = rig.Fixture.Settings;
        var current = await settings.GetAsync();
        var version = current.Headers.ETag!.Tag.Trim('"');
        var onlyCop = settings.Body(body =>
        {
            body["currencies"] = new[] { settings.CurrencyEntry("COP", "Bancolombia", "A-COP-1") };
            body["primaryCurrency"] = "COP";
        });
        Assert.Equal(HttpStatusCode.OK, (await settings.PutAsync(onlyCop, version)).StatusCode);
        var customersBefore = await rig.CountAsync("customers.customers");

        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, NewCustomerBody), HttpStatusCode.UnprocessableEntity, "store.order_request.currency_not_in_store");

        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.New, state.Status);
        Assert.False(state.IsBeingTaken(rig.Clock.UtcNow));
        Assert.Equal(0, await rig.CountAsync("quotations.orders"));
        Assert.Equal(customersBefore, await rig.CountAsync("customers.customers"));
    }

    // ---- Documentos por país: NIF, NIE y TAX_ID de punta a punta ----------------------------------------------------------

    private static Task<Guid> SubmitSpanishAsync(
        StoreInboxRig rig, string type = "NIF", string number = "12345678Z", string address = "Calle Mayor 1") =>
        rig.SubmitAsync(billing =>
        {
            billing["identificationType"] = type;
            billing["identificationNumber"] = number;
            billing["country"] = "ES";
            billing["departmentId"] = null;
            billing["cityId"] = null;
            billing["cityName"] = "Madrid";
            billing["phone"] = "+34600111222";
            billing["address"] = address;
        });

    private static Task<string?> BillingPartyAsync(StoreInboxRig rig, Guid orderId) =>
        rig.TextAsync(
            "SELECT identification_type || '|' || country FROM quotations.order_parties WHERE order_id = @id AND role = 'Billing'",
            ("id", orderId));

    [Theory]
    [InlineData("ES", "NIF", "Nif", "12345678Z", "Madrid", "+34600111222")]
    [InlineData("ES", "NIE", "Nie", "X1234567L", "Madrid", "+34600111222")]
    [InlineData("US", "TAX_ID", "TaxId", "12-3456789", "Miami", "+13055550100")]
    public async Task TakingANewCustomerWithAForeignDocumentKeepsItEverywhere(
        string country, string wire, string member, string number, string city, string phone)
    {
        await using var rig = await StoreInboxRig.OpenAsync((_, body) => body["salesCountries"] = ColombiaSpainAndUnitedStates);
        var request = await rig.SubmitAsync(billing =>
        {
            billing["identificationType"] = wire;
            billing["identificationNumber"] = number;
            billing["country"] = country;
            billing["departmentId"] = null;
            billing["cityId"] = null;
            billing["cityName"] = city;
            billing["phone"] = phone;
            billing["address"] = "Calle Mayor 1";
        });

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        var customer = await rig.CustomerAsync((Guid)taken["customerId"]!);
        Assert.Equal((member, country, number), (customer.IdentificationType, customer.Country, customer.IdentificationNumber));
        var orderId = (Guid)taken["orderId"]!;
        Assert.Equal($"{member}|{country}", await BillingPartyAsync(rig, orderId));
        Assert.Equal(wire, (string)Party(await rig.OrderAsync(orderId), "Billing")["identificationType"]!);
        Assert.Equal(StoreOrderRequestStatus.Converted, (await rig.StateAsync(request)).Status);
    }

    [Fact]
    public async Task TakingAnIncompleteCustomerWithoutDocumentGivesItTheNifAndSpain()
    {
        await using var rig = await StoreInboxRig.OpenAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);
        var customerId = await rig.SeedIncompleteAsync("Pedro WhatsApp", phone: "+57 310 111 2233", phoneE164: "+573101112233", country: "CO");
        var request = await SubmitSpanishAsync(rig);

        var taken = await rig.TakeOkAsync(request, ExistingBody(customerId));

        var customer = await rig.CustomerAsync(customerId);
        Assert.Equal(("Complete", "Nif", "ES", "12345678Z"), (customer.Completeness, customer.IdentificationType, customer.Country, customer.IdentificationNumber));
        Assert.Equal("Nif|ES", await BillingPartyAsync(rig, (Guid)taken["orderId"]!));
    }

    [Theory]
    [InlineData("PASAPORTE", "X1234567")]
    [InlineData("NIF", "12345678Z")]
    public async Task AnIncompleteCustomerWithAnAddressAndCountryCoTakesTheSpanishRequestAddressAndCountry(string type, string number)
    {
        await using var rig = await StoreInboxRig.OpenAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);
        var customerId = await rig.SeedIncompleteAsync(
            "Pedro WhatsApp", phone: "+57 310 111 2233", phoneE164: "+573101112233", address: "Carrera 7 # 8-9", country: "CO");
        var request = await SubmitSpanishAsync(rig, type, number, "Calle Mayor 1");

        await rig.TakeOkAsync(request, ExistingBody(customerId));

        var customer = await rig.CustomerAsync(customerId);
        Assert.Equal(("Calle Mayor 1", "ES"), (customer.Address, customer.Country));
    }

    [Fact]
    public async Task TakingACompleteColombianCustomerForASpanishRequestLeavesItAlone()
    {
        await using var rig = await StoreInboxRig.OpenAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);
        var customerId = await rig.CreateCompleteCustomerAsync("Laura", "1020304050", "3105550001");
        var before = await rig.CustomerAsync(customerId);
        var request = await SubmitSpanishAsync(rig);

        var taken = await rig.TakeOkAsync(request, ExistingBody(customerId));

        var after = await rig.CustomerAsync(customerId);
        Assert.Equal(("Cc", "CO", "1020304050"), (after.IdentificationType, after.Country, after.IdentificationNumber));
        Assert.Equal(
            (before.Name, before.Phone, before.Email, before.Address, before.CityId, before.Completeness),
            (after.Name, after.Phone, after.Email, after.Address, after.CityId, after.Completeness));
        // La versión sube sólo por la entrada que «Tomar» agrega a la libreta (AddAddress toca al cliente), no por la ficha.
        Assert.Contains(await rig.AddressesAsync(customerId), book => book.Country == "ES" && book.Address == "Calle Mayor 1");
        Assert.Equal("Nif|ES", await BillingPartyAsync(rig, (Guid)taken["orderId"]!));
    }

    [Fact]
    public async Task TakingTheCustomerThatAlreadyHasTheNifCreatesNoSecondCustomer()
    {
        await using var rig = await StoreInboxRig.OpenAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);
        var customerId = await rig.CreateCompleteCustomerAsync(
            "Laura", "12345678Z", "+34600111222", identificationType: "NIF", country: "ES", cityName: "Madrid");
        var request = await SubmitSpanishAsync(rig);

        var taken = await rig.TakeOkAsync(request, ExistingBody(customerId));

        Assert.Equal(1, await rig.CountAsync("customers.customers"));
        Assert.False((bool)taken["customerCreated"]!);
        Assert.Equal(customerId, (Guid)taken["customerId"]!);
        Assert.Equal("Nif|ES", await BillingPartyAsync(rig, (Guid)taken["orderId"]!));
    }

    [Fact]
    public async Task TheCandidatesOfANifRequestIncludeTheCustomerWithThatDocument()
    {
        await using var rig = await StoreInboxRig.OpenAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);
        var customerId = await rig.CreateCompleteCustomerAsync(
            "Laura", "12345678Z", "+34600111222", identificationType: "NIF", country: "ES", cityName: "Madrid");
        var request = await SubmitSpanishAsync(rig);

        var candidates = await rig.GetJsonAsync($"/{request}/customer-candidates");

        var candidate = candidates["candidates"]!.AsArray().Single(item => (Guid)item!["id"]! == customerId);
        Assert.Contains("Document", candidate!["matchedBy"]!.AsArray().Select(value => (string)value!));
    }
}
