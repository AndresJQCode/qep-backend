using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Store.Application;
using Modules.Store.Domain;
using static Modules.Store.UnitTests.StoreTestData;

namespace Modules.Store.UnitTests;

/// <summary>El checkout crea el pedido (spec 2026-11-09 §4.3): todo lo que rechaza va antes de escribir, y Store sólo escribe después.</summary>
public sealed class SubmitOrderRequestHandlerTests
{
    private static readonly Guid ExistingCustomerId = Guid.Parse("c3333333-3333-3333-3333-333333333333");

    private static CheckoutRig StoreWithAdvisor(Guid advisor) => new(ConfiguredStore(Input(advisorMembershipId: advisor)));

    [Fact]
    public async Task ARejectedCurrencyWritesNothingAndCallsNoCaptcha()
    {
        var rig = new CheckoutRig();

        var error = await Assert.ThrowsAsync<StoreDomainException>(() => rig.SubmitAsync(CheckoutRig.Body(currency: "USD")));

        Assert.Equal(StoreErrorCodes.PublicCurrencyInvalid, error.Code);
        Assert.Equal(0, rig.Captcha.Calls);
        Assert.Empty(rig.Orders.Drafts);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task ARetryReturnsTheExistingOrderWithoutCaptchaCustomerOrWrites()
    {
        var rig = new CheckoutRig();
        var existingId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        rig.Orders.Existing = new StoreOrderCreated(existingId, "PED-0042", 15_000m, "EUR", ExistingCustomerId, Member, true);

        var (receipt, created) = await rig.SubmitAsync(
            CheckoutRig.Body(createAccount: new PublicBuyerSignupInput("una-clave-segura", true)));

        Assert.Equal(new PublicOrderRequestReceiptDto(existingId, "PED-0042", 15_000m, "EUR"), receipt);
        Assert.False(created);
        Assert.Equal(0, rig.Captcha.Calls);
        Assert.Empty(rig.Customers.CreateCalls);
        Assert.Empty(rig.Consents.Added);
        Assert.Empty(rig.Outbox.Messages);
        Assert.Equal(["orders.find"], rig.Log.Entries);
    }

    [Fact]
    public async Task TheDailyCapIsCheckedOnStoreOrders()
    {
        var rig = new CheckoutRig();
        rig.Orders.Count = 500;

        var error = await Assert.ThrowsAsync<RequestRateLimitedException>(() => rig.SubmitAsync(CheckoutRig.Body()));

        Assert.Equal(StoreErrorCodes.DailyCap, error.Code);
        Assert.Empty(rig.Customers.CreateCalls);
        Assert.Equal(["orders.find", "captcha", "orders.count"], rig.Log.Entries);
    }

    [Fact]
    public async Task AnUnavailableLineIs422BeforeTouchingTheCustomer()
    {
        var rig = new CheckoutRig();
        rig.Catalog.Sellable.Clear();

        var error = await Assert.ThrowsAsync<StoreDomainException>(() => rig.SubmitAsync(CheckoutRig.Body()));

        Assert.Equal(StoreErrorCodes.LineUnavailable, error.Code);
        Assert.Empty(rig.Customers.CreateCalls);
        Assert.Empty(rig.Customers.IncompleteCalls);
        Assert.Equal(1, rig.Captcha.Calls);
        Assert.DoesNotContain("customers.find", rig.Log.Entries);
    }

    [Fact]
    public async Task AConfiguredActiveAdvisorIsUsed()
    {
        var rig = StoreWithAdvisor(Member);
        rig.Members.Active.Add(Member);

        await rig.SubmitAsync(CheckoutRig.Body());

        Assert.Equal(Member, Assert.Single(rig.Orders.Drafts).AdvisorMembershipId);
        Assert.Empty(rig.Logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task AConfiguredAdvisorThatIsNoLongerActiveFallsBackToTheOwnerWithAWarning()
    {
        var rig = StoreWithAdvisor(Member);
        rig.Members.Owner = OtherMember;

        await rig.SubmitAsync(CheckoutRig.Body());

        Assert.Equal(OtherMember, Assert.Single(rig.Orders.Drafts).AdvisorMembershipId);
        var warning = Assert.Single(rig.Logger.At(LogLevel.Warning));
        Assert.Contains(Member.ToString(), warning, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@test.co", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAConfiguredAdvisorTheOwnerIsUsed()
    {
        var rig = new CheckoutRig();
        rig.Members.Owner = OtherMember;

        await rig.SubmitAsync(CheckoutRig.Body());

        Assert.Equal(OtherMember, Assert.Single(rig.Orders.Drafts).AdvisorMembershipId);
        Assert.Empty(rig.Logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task AnExistingCompleteCustomerIsUsedUntouched()
    {
        var rig = new CheckoutRig();
        rig.Customers.ByDocument = new StoreCustomerRef(ExistingCustomerId, true, WithRetention: true, VatSurplus: true);

        await rig.SubmitAsync(CheckoutRig.Body());

        var draft = Assert.Single(rig.Orders.Drafts);
        Assert.Equal(ExistingCustomerId, draft.ClientId);
        Assert.True(draft.BillingWithRetention);
        Assert.True(draft.BillingVatSurplus);
        Assert.Empty(rig.Customers.CreateCalls);
        Assert.Empty(rig.Customers.CompleteCalls);
        Assert.Empty(rig.Customers.IncompleteCalls);
    }

    [Fact]
    public async Task AnExistingIncompleteCustomerIsCompleted()
    {
        var rig = new CheckoutRig();
        rig.Customers.ByDocument = new StoreCustomerRef(ExistingCustomerId, false, false, false);

        await rig.SubmitAsync(CheckoutRig.Body());

        var call = Assert.Single(rig.Customers.CompleteCalls);
        Assert.Null(call.ActorUserId);
        Assert.Equal(ExistingCustomerId, call.CustomerId);
        Assert.Equal("CC", call.Billing.IdentificationType);
        Assert.Equal("123456", call.Billing.IdentificationNumber);
        Assert.Equal("Ana Pérez", call.Billing.Name);
        Assert.Equal(CheckoutRig.CityId, call.Billing.CityId);
        Assert.Null(call.Billing.CityName);
        Assert.Empty(rig.Customers.CreateCalls);
        Assert.Equal(ExistingCustomerId, Assert.Single(rig.Orders.Drafts).ClientId);
    }

    [Fact]
    public async Task AMissingCustomerIsCreatedComplete()
    {
        var rig = new CheckoutRig();

        await rig.SubmitAsync(CheckoutRig.Body());

        var call = Assert.Single(rig.Customers.CreateCalls);
        Assert.Null(call.ActorUserId);
        Assert.Null(call.Data.ClassificationId);
        Assert.Equal("123456", call.Data.IdentificationNumber);
        Assert.Empty(rig.Customers.CompleteCalls);
        Assert.Equal(FakeCustomers.CreatedId, Assert.Single(rig.Orders.Drafts).ClientId);
    }

    [Fact]
    public async Task WhenCustomersRejectsTheCompleteProfileAnIncompleteIsCreatedAndTheOrderFollows()
    {
        var rig = new CheckoutRig();
        rig.Customers.CreateOutcome = StoreCustomerCreationOutcome.NotCompletable;

        await rig.SubmitAsync(CheckoutRig.Body());

        Assert.Equal(("Ana Pérez", "300 123 4567", "ana@test.co", "CO"), Assert.Single(rig.Customers.IncompleteCalls));
        Assert.Equal(FakeCustomers.IncompleteId, Assert.Single(rig.Orders.Drafts).ClientId);
        Assert.Contains(rig.Logger.At(LogLevel.Information), message => message.Contains("NotCompletable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADocumentOfAnInactiveCustomerCreatesANewIncompleteWithoutDocument()
    {
        var rig = new CheckoutRig();
        rig.Customers.CreateOutcome = StoreCustomerCreationOutcome.DocumentOwnerUnavailable;

        await rig.SubmitAsync(CheckoutRig.Body());

        Assert.Equal(("Ana Pérez", "300 123 4567", "ana@test.co", "CO"), Assert.Single(rig.Customers.IncompleteCalls));
        Assert.Single(rig.Orders.Drafts);
        Assert.Empty(rig.Customers.CompleteCalls);
        var logged = Assert.Single(rig.Logger.At(LogLevel.Information), message => message.Contains("DocumentOwnerUnavailable", StringComparison.Ordinal));
        Assert.DoesNotContain("123456", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARaceOnTheDocumentUsesTheWinnerAndCompletesIt()
    {
        var rig = new CheckoutRig();
        rig.Customers.CreateOutcome = StoreCustomerCreationOutcome.ExistingByDocument;
        rig.Customers.CreatedCustomer = new StoreCustomerRef(ExistingCustomerId, false, false, false);

        await rig.SubmitAsync(CheckoutRig.Body());

        Assert.Equal(ExistingCustomerId, Assert.Single(rig.Customers.CompleteCalls).CustomerId);
        Assert.Empty(rig.Customers.IncompleteCalls);
        Assert.Equal(ExistingCustomerId, Assert.Single(rig.Orders.Drafts).ClientId);
    }

    [Fact]
    public async Task BillingAndADifferentShippingMakeTwoAddresses()
    {
        var rig = new CheckoutRig();

        await rig.SubmitAsync(CheckoutRig.Body(shipping: CheckoutRig.Shipping()));

        var addresses = Assert.Single(rig.Customers.AddressCalls);
        Assert.Equal(2, addresses.Count);
        Assert.Equal("+573001234567", addresses[0].Phone);
        Assert.Equal("Luis Gómez", addresses[1].Name);
        Assert.Equal("+573007654321", addresses[1].Phone);
    }

    [Fact]
    public async Task WithoutShippingThereIsOneAddress()
    {
        var rig = new CheckoutRig();

        await rig.SubmitAsync(CheckoutRig.Body());

        Assert.Single(Assert.Single(rig.Customers.AddressCalls));
    }

    [Fact]
    public async Task SkippedAddressesDoNotStopTheOrder()
    {
        var rig = new CheckoutRig();
        rig.Customers.AddressOutcomes.Add(StoreAddressOutcome.Skipped);

        await rig.SubmitAsync(CheckoutRig.Body());

        Assert.Single(rig.Orders.Drafts);
        var logged = Assert.Single(rig.Logger.At(LogLevel.Information));
        Assert.DoesNotContain("Calle 1", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOrderDraftCarriesTheKeyAndTheParties()
    {
        var rig = new CheckoutRig();
        var body = CheckoutRig.Body();

        await rig.SubmitAsync(body);

        var draft = Assert.Single(rig.Orders.Drafts);
        Assert.Equal(TenantId, draft.TenantId);
        Assert.Equal(body.ClientRequestId, draft.ClientRequestId);
        Assert.True(draft.IsRetail);
        Assert.Equal("COP", draft.BillingAccount.Currency);
        Assert.Equal("123456", draft.Billing.IdentificationNumber);
        Assert.Equal("CC", draft.Billing.IdentificationType);
        Assert.Equal("300 123 4567", draft.Billing.Phone);
        Assert.Equal(CheckoutRig.DepartmentId, draft.Billing.DepartmentId);
        Assert.Null(draft.Billing.CityName);
        Assert.Null(draft.Shipping.IdentificationNumber);
        Assert.Null(draft.Shipping.IdentificationType);
        Assert.Equal("ana@test.co", draft.Shipping.Email);
        Assert.Equal(body.Note, draft.Notes);
        Assert.Equal(new StoreOrderLineInput(CheckoutRig.VariantId, 2m), Assert.Single(draft.Lines));
    }

    [Fact]
    public async Task AfterTheOrderStoreRecordsConsentAuditAndSignupInOneSave()
    {
        var rig = new CheckoutRig();

        await rig.SubmitAsync(CheckoutRig.Body(createAccount: new PublicBuyerSignupInput("una-clave-segura", true)));

        var consent = Assert.Single(rig.Consents.Added);
        Assert.Equal(FakeOrders.OrderId, consent.OrderId);
        Assert.Equal(TenantId, consent.TenantId);
        Assert.Equal(CheckoutRig.VisitorIp, consent.Consent.Ip);
        Assert.Equal(Now, consent.Consent.At);
        Assert.Equal(rig.Store.DataPolicyUrl, consent.Consent.PolicyUrl);
        Assert.Equal(rig.Store.DataPolicySetAt, consent.Consent.PolicySetAt);
        Assert.Equal(("store.order.created", "order", FakeOrders.OrderId.ToString()), Assert.Single(rig.Audit.Anonymous));
        var (eventName, payload) = Assert.Single(rig.Outbox.Messages);
        Assert.Equal(StoreIntegrationEvents.BuyerSignupRequested, eventName);
        var signup = Assert.IsType<StoreBuyerSignupRequestedPayload>(payload);
        Assert.Equal(FakeOrders.OrderId, signup.OrderId);
        Assert.Equal("hash", signup.PasswordHash);
        Assert.Equal(1, rig.UnitOfWork.Saves);
        Assert.Equal(
            [
                "orders.find", "captcha", "orders.count", "orders.quote", "customers.find", "customers.create", "customers.addresses",
                "orders.create", "consents.add", "audit.anonymous", "outbox.add", "uow.save",
            ],
            rig.Log.Entries);
    }

    [Fact]
    public async Task ALostKeyRaceReturnsTheWinnerAndWritesNothingInStore()
    {
        var rig = new CheckoutRig();
        rig.Orders.AlreadyExisted = true;

        var (receipt, created) = await rig.SubmitAsync(
            CheckoutRig.Body(createAccount: new PublicBuyerSignupInput("una-clave-segura", true)));

        Assert.Equal(new PublicOrderRequestReceiptDto(FakeOrders.OrderId, "PED-0001", 21_000m, "COP"), receipt);
        Assert.False(created);
        Assert.Empty(rig.Consents.Added);
        Assert.Empty(rig.Audit.Anonymous);
        Assert.Empty(rig.Outbox.Messages);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task TheReceiptIsTheOrder()
    {
        var rig = new CheckoutRig();

        var (receipt, created) = await rig.SubmitAsync(CheckoutRig.Body());

        Assert.Equal(new PublicOrderRequestReceiptDto(FakeOrders.OrderId, "PED-0001", 21_000m, "COP"), receipt);
        Assert.True(created);
    }
}
