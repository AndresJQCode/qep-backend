using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>Spec de Shopify 2026-10-15 §6.2.1 pasos 3 y 4 sin base: el alta de un comprador desde una integración
/// repite el orden de <c>CreateCustomerHandler</c> (clasificación y ciudad antes del CUC), gana la carrera del documento
/// releyendo y audita con actor <c>Integration</c>.</summary>
public sealed class CustomerProvisioningTests
{
    private sealed record Bed(
        CustomerProvisioning Provisioning, FakeCustomerRepository Customers, RecordingAudit Audit, List<string> Calls);

    private static Bed NewBed(Customer? existing = null, Exception? saveFailure = null)
    {
        var calls = new List<string>();
        var customers = new FakeCustomerRepository { Existing = existing };
        var audit = new RecordingAudit();
        var provisioning = new CustomerProvisioning(
            customers,
            new Classifications(calls),
            new Geography(calls),
            new UnitOfWork(saveFailure),
            audit,
            new Cucs(calls),
            new FixedClock(),
            new CreateCustomerValidator(),
            new PlusNormalizer());
        return new Bed(provisioning, customers, audit, calls);
    }

    private static CompleteCustomerData Buyer(string? email = "Laura@Verde.co") =>
        new("Laura Pérez", "CC", "1020304050", "310 935 2187", email, "Calle 10 # 45-12", "CO", CityId, null, Classification.Id.Value);

    [Fact]
    public async Task CreateCompleteIssuesCucAfterClassificationAndCity()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Equal(["classification", "city", "cuc"], bed.Calls);
        var customer = Assert.Single(bed.Customers.Added);
        Assert.Equal(new ProvisionedCustomer(customer.Id.Value, true, true, true, 1), result);
        Assert.Equal("CLI08000007", customer.Cuc);
        Assert.Equal(Tenant, customer.TenantId);
        Assert.False(customer.WithRetention);
        Assert.False(customer.VatSurplus);
        Assert.Equal("+573109352187", customer.PhoneE164);
        // La libreta nace con la dirección del alta, como en el POST.
        Assert.Equal(CityId, Assert.Single(customer.Addresses).CityId);
    }

    [Fact]
    public async Task CreateCompleteInvalidDataFailsBeforeIssuingCuc()
    {
        var bed = NewBed();

        await Assert.ThrowsAsync<ValidationException>(() =>
            bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(email: "no-es-un-correo"), TestContext.Current.CancellationToken));

        Assert.Empty(bed.Calls);
        Assert.Empty(bed.Customers.Added);
    }

    [Fact]
    public async Task CreateCompleteIdentificationTakenRaceReturnsExisting()
    {
        var existing = Customer.Create(
            CustomerId.New(), Tenant, "CLI08000001", "Laura", null, null,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            null,
            new CustomerContactInfo { Phone = "3000000000", Email = "otra@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now);
        var bed = NewBed(existing, new CustomersDomainException("customers.customer.identification_taken", "taken"));

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Equal(new ProvisionedCustomer(existing.Id.Value, true, true, false, existing.Version), result);
    }

    [Fact]
    public async Task CreateCompleteOtherDomainFailureIsNotSwallowed()
    {
        var bed = NewBed(saveFailure: new CustomersDomainException("customers.customer.cuc_taken", "taken"));

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.cuc_taken", failure.Code);
    }

    [Fact]
    public async Task CreateCompleteAuditsWithIntegrationActor()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Empty(bed.Audit.Human);
        Assert.Equal((Tenant, "customers.customer.created", result.Id.ToString(), "success"), Assert.Single(bed.Audit.ByIntegration));
    }

    [Fact]
    public async Task CreateIncompleteWithoutNameUsesShopifyDefault()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateIncompleteAsync(
            Tenant, "  ", "310 935 2187", "laura@verde.co", "CO", TestContext.Current.CancellationToken);

        var customer = Assert.Single(bed.Customers.Added);
        Assert.Equal(new ProvisionedCustomer(customer.Id.Value, false, true, true, 1), result);
        Assert.Equal(CustomerProvisioning.ExternalSaleFallbackName, customer.Name);
        Assert.Equal("Cliente de Shopify", CustomerProvisioning.ExternalSaleFallbackName);
        Assert.Equal("+573109352187", customer.PhoneE164);
        Assert.Empty(bed.Calls);
        Assert.Equal((Tenant, "customers.customer.created", result.Id.ToString(), "success"), Assert.Single(bed.Audit.ByIntegration));
    }

    [Fact]
    public async Task CreateIncompleteTrimsLongNameToTheColumn()
    {
        var bed = NewBed();

        await bed.Provisioning.CreateIncompleteAsync(
            Tenant, new string('a', Customer.NameMaxLength + 10), null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(Customer.NameMaxLength, Assert.Single(bed.Customers.Added).Name.Length);
    }

    [Fact]
    public async Task CreateFromRegisterAddsAnIncompleteAndAuditsTheCashierAsActor()
    {
        var bed = NewBed();
        var cashierId = Guid.NewGuid();

        var result = await bed.Provisioning.CreateFromRegisterAsync(
            Tenant, cashierId, new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null), source: null, TestContext.Current.CancellationToken);

        var customer = Assert.Single(bed.Customers.Added);
        Assert.False(customer.IsComplete);
        Assert.Equal(new ProvisionedCustomer(customer.Id.Value, false, true, true, 1), result);
        Assert.Empty(bed.Audit.ByIntegration);
        Assert.Equal(
            (Tenant, cashierId, "customers.customer.created", customer.Id.ToString(), "success", (string?)null),
            Assert.Single(bed.Audit.Human));
    }

    [Fact]
    public async Task RegisterWithAnEmailStoresIt()
    {
        // Spec cobros-recurrentes §5: la importación de Billing da el alta con el correo de la fila.
        var bed = NewBed();

        await bed.Provisioning.CreateFromRegisterAsync(
            Tenant,
            Guid.NewGuid(),
            new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null, " Laura@Verde.co "),
            "billing",
            TestContext.Current.CancellationToken);

        var customer = Assert.Single(bed.Customers.Added);
        Assert.Equal("laura@verde.co", customer.Email);
        Assert.False(customer.IsComplete);
    }

    [Fact]
    public async Task CreateFromRegisterLetsTheIdentificationRaceThrough()
    {
        var bed = NewBed(saveFailure: new CustomersDomainException("customers.customer.identification_taken", "taken"));

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() => bed.Provisioning.CreateFromRegisterAsync(
            Tenant, Guid.NewGuid(), new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null), source: null, TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.identification_taken", failure.Code);
    }

    [Fact]
    public async Task CreateFromRegisterRejectsAnUnknownDocumentType()
    {
        var bed = NewBed();

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() => bed.Provisioning.CreateFromRegisterAsync(
            Tenant, Guid.NewGuid(), new RegisterCustomerData("Laura Gómez", "RUT", "1020304050", null), source: null, TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.identification_type_invalid", failure.Code);
        Assert.Empty(bed.Customers.Added);
    }

    [Fact]
    public async Task CreateFromRegisterAuditsWithTheGivenSource()
    {
        var bed = NewBed();

        await bed.Provisioning.CreateFromRegisterAsync(
            Tenant, Guid.NewGuid(), new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null), "pos", TestContext.Current.CancellationToken);

        Assert.Equal("pos", Assert.Single(bed.Audit.Human).Source);
    }

    [Fact]
    public async Task CreateFromRegisterWithoutSourceAuditsWithTheDefault()
    {
        var bed = NewBed();

        await bed.Provisioning.CreateFromRegisterAsync(
            Tenant, Guid.NewGuid(), new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null), source: null, TestContext.Current.CancellationToken);

        // El publisher real resuelve null a «customers».
        Assert.Null(Assert.Single(bed.Audit.Human).Source);
    }

    [Fact]
    public async Task OtherProvisioningPathsKeepAuditingWithoutSource()
    {
        var bed = NewBed();

        await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);
        await bed.Provisioning.CreateIncompleteAsync(Tenant, "Laura", null, null, null, TestContext.Current.CancellationToken);

        // Las dos van por PublishByIntegration, que no lleva source: sale «customers» como siempre.
        Assert.Empty(bed.Audit.Human);
        Assert.Equal(2, bed.Audit.ByIntegration.Count);
    }

    // ---- SC-05: completar y corregir la ciudad de un cliente de venta externa -------------------------------------

    private static Customer NewIncomplete() =>
        Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), Tenant, "Laura Pérez", "310 935 2187", "laura@verde.co", "CO", Now, new PlusNormalizer());

    private static Customer NewComplete(Guid? cityId = null) =>
        Customer.Create(
            CustomerId.New(), Tenant, "CLI08000001", "Laura", null,
            new CustomerAddressDetails { Name = "Laura", Address = "Calle 1", CityId = cityId ?? CityId },
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            null,
            new CustomerContactInfo { Phone = "3000000000", Email = "otra@verde.co", Address = "Calle 1", Country = "CO", CityId = cityId ?? CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now);

    private static void AssertUntouched(Customer customer, long version, DateTimeOffset updatedAt, Bed bed)
    {
        Assert.Equal(version, customer.Version);
        Assert.Equal(updatedAt, customer.UpdatedAt);
        Assert.Empty(bed.Audit.ByIntegration);
        Assert.Empty(bed.Audit.Human);
    }

    [Fact]
    public async Task CreateCompleteReportsVersionOne()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Version);
        Assert.Equal(Assert.Single(bed.Customers.Added).Version, result.Version);
    }

    [Fact]
    public async Task CompleteExternalSaleIssuesTheCucWithTheDepartmentOfTheCity()
    {
        var customer = NewIncomplete();
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer() with { CityId = ValleCityId }, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.Applied, 2, null, ValleCityId), outcome);
        Assert.Equal(["classification", "city", "cuc"], bed.Calls);
        // prefijo de la clasificación + departamento de Cali (76) + consecutivo
        Assert.Equal("CLI76000007", customer.Cuc);
        Assert.True(customer.IsComplete);
        Assert.Equal(ValleCityId, customer.CityId);
        Assert.Equal(ValleCityId, Assert.Single(customer.Addresses).CityId);
        Assert.True(Assert.Single(customer.Addresses).IsPrincipal);
        Assert.Equal(2, customer.Version);
        Assert.Empty(bed.Customers.Added);
        Assert.Equal(
            (Tenant, CustomerAuditActions.Completed, customer.Id.ToString(), "success"),
            Assert.Single(bed.Audit.ByIntegration));
        Assert.Empty(bed.Audit.Human);
    }

    [Fact]
    public async Task CompleteExternalSaleTwiceIsAlreadyApplied()
    {
        var customer = NewIncomplete();
        var bed = NewBed(customer);
        var data = Buyer();

        var first = await bed.Provisioning.CompleteExternalSaleAsync(Tenant, customer.Id.Value, 1, data, [], TestContext.Current.CancellationToken);
        var calls = bed.Calls.Count;
        var second = await bed.Provisioning.CompleteExternalSaleAsync(Tenant, customer.Id.Value, 1, data, [], TestContext.Current.CancellationToken);

        Assert.Equal(ExternalCustomerCityOutcomeKind.Applied, first.Kind);
        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.AlreadyApplied, 2, null, CityId), second);
        // El segundo intento no gasta otro número del consecutivo ni audita otra vez.
        Assert.Equal(calls, bed.Calls.Count);
        Assert.Single(bed.Audit.ByIntegration);
        Assert.Equal(2, customer.Version);
    }

    // Spec 2026-10-26: el intento anterior completó al cliente con la ciudad que el sinónimo tenía entonces y cayó antes de que
    // Shopify guardara; el reintento manda la ciudad nueva. Es la misma escritura (versión siguiente, mismos datos salvo la ciudad):
    // AlreadyApplied con la ciudad que quedó guardada, para que Shopify la guarde en su huella y la recorrija.
    [Fact]
    public async Task CompleteExternalSaleRetriedWithAnotherCityIsAlreadyAppliedWithTheWrittenCity()
    {
        var customer = NewIncomplete();
        var bed = NewBed(customer);
        await bed.Provisioning.CompleteExternalSaleAsync(Tenant, customer.Id.Value, 1, Buyer(), [], TestContext.Current.CancellationToken);
        var calls = bed.Calls.Count;

        var retried = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer() with { CityId = ValleCityId }, [CityId], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.AlreadyApplied, 2, null, CityId), retried);
        Assert.Equal(CityId, customer.CityId);
        Assert.Equal(calls, bed.Calls.Count);
        Assert.Single(bed.Audit.ByIntegration);
    }

    // Una persona lo completó con el documento de la venta pero en otra ciudad, justo después de la importación (versión 2). Esa
    // ciudad no la pidió nunca este trabajo: no es su escritura, aunque la versión y los datos coincidan.
    [Fact]
    public async Task CompleteExternalSaleOfACustomerAPersonCompletedInAnotherCityIsNotClaimed()
    {
        var customer = NewIncomplete();
        var bed = NewBed(customer);
        await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer() with { CityId = ValleCityId }, [], TestContext.Current.CancellationToken);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer(), [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Complete), outcome);
        Assert.Equal(ValleCityId, customer.CityId);
    }

    // Una persona lo completó con otro documento justo después de la importación (versión 2): no es nuestra escritura.
    [Fact]
    public async Task CompleteExternalSaleOfACustomerCompletedWithOtherDataIsNotEligible()
    {
        var customer = NewIncomplete();
        var bed = NewBed(customer);
        await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer() with { IdentificationNumber = "9988776655" }, [], TestContext.Current.CancellationToken);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer(), [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Complete), outcome);
    }

    [Fact]
    public async Task CompleteExternalSaleOfAnUnknownCustomerIsNotFound()
    {
        var bed = NewBed();

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, Guid.NewGuid(), 1, Buyer(), [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotFound, null, null), outcome);
        Assert.Empty(bed.Calls);
    }

    [Fact]
    public async Task CompleteExternalSaleOnAnEditedCustomerIsNotEligible()
    {
        var customer = NewIncomplete();
        customer.Deactivate(Now.AddMinutes(1));
        customer.Activate(Now.AddMinutes(2));
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer(), [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Edited), outcome);
        Assert.Equal("edited", outcome.Reason);
        Assert.False(customer.IsComplete);
        Assert.Null(customer.Cuc);
        Assert.Empty(bed.Calls);
        AssertUntouched(customer, 3, updatedAt, bed);
    }

    [Fact]
    public async Task CompleteExternalSaleOfAnInactiveCustomerIsNotEligible()
    {
        var customer = NewIncomplete();
        customer.Deactivate(Now.AddMinutes(1));
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 2, Buyer(), [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Inactive), outcome);
        Assert.Equal("inactive", outcome.Reason);
        Assert.Empty(bed.Calls);
        AssertUntouched(customer, 2, updatedAt, bed);
    }

    [Fact]
    public async Task CompleteExternalSaleOfAMergedCustomerIsNotEligible()
    {
        var customer = NewIncomplete();
        CustomerMergePlan.Create(NewComplete(), [customer]).MarkMerged(Now.AddMinutes(1));
        var version = customer.Version;
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, version, Buyer(), [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Merged), outcome);
        Assert.Equal("merged", outcome.Reason);
        Assert.Empty(bed.Calls);
        AssertUntouched(customer, version, updatedAt, bed);
    }

    [Fact]
    public async Task CompleteExternalSaleOfACompleteCustomerWithAnotherCityIsNotEligible()
    {
        var customer = NewComplete();
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer() with { CityId = ValleCityId }, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Complete), outcome);
        Assert.Equal("complete", outcome.Reason);
        Assert.Equal(CityId, customer.CityId);
        Assert.Empty(bed.Calls);
        AssertUntouched(customer, 1, updatedAt, bed);
    }

    [Fact]
    public async Task CompleteExternalSaleWithoutPhoneIsNotCompletable()
    {
        // El brief decía «sin correo», pero el correo es opcional en CustomerWriteRules: lo obligatorio es el teléfono.
        var customer = NewIncomplete();
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer() with { Phone = null }, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotCompletable, null, "validation.failed"), outcome);
        // Sin CUC quemado: la validación corre antes de pedir el consecutivo.
        Assert.Empty(bed.Calls);
        Assert.False(customer.IsComplete);
        Assert.Null(customer.Cuc);
        AssertUntouched(customer, 1, updatedAt, bed);
    }

    [Fact]
    public async Task CompleteExternalSaleWithAnUnknownClassificationIsNotCompletable()
    {
        var customer = NewIncomplete();
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer() with { ClassificationId = Guid.NewGuid() }, [], TestContext.Current.CancellationToken);

        Assert.Equal(
            new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotCompletable, null, CustomersErrorCodes.ClassificationNotFound),
            outcome);
        Assert.Equal("customers.customer.classification_not_found", outcome.Reason);
        Assert.DoesNotContain("cuc", bed.Calls);
        AssertUntouched(customer, 1, updatedAt, bed);
    }

    [Fact]
    public async Task CompleteExternalSaleWithAnUnknownCityIsNotCompletable()
    {
        var customer = NewIncomplete();
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer() with { CityId = Guid.NewGuid() }, [], TestContext.Current.CancellationToken);

        Assert.Equal(
            new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotCompletable, null, CustomersErrorCodes.CityNotFound),
            outcome);
        Assert.Equal("customers.customer.city_not_found", outcome.Reason);
        Assert.DoesNotContain("cuc", bed.Calls);
        AssertUntouched(customer, 1, updatedAt, bed);
    }

    [Fact]
    public async Task CompleteExternalSaleWithATakenDocumentIsNotCompletable()
    {
        var customer = NewIncomplete();
        var bed = NewBed(customer, new CustomersDomainException(CustomersErrorCodes.IdentificationTaken, "taken"));

        var outcome = await bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer(), [], TestContext.Current.CancellationToken);

        Assert.Equal(
            new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotCompletable, null, CustomersErrorCodes.IdentificationTaken),
            outcome);
        Assert.Equal("customers.customer.identification_taken", outcome.Reason);
    }

    [Fact]
    public async Task CompleteExternalSaleLetsOtherDomainFailuresThrough()
    {
        var customer = NewIncomplete();
        var bed = NewBed(customer, new CustomersDomainException("customers.customer.cuc_taken", "taken"));

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() => bed.Provisioning.CompleteExternalSaleAsync(
            Tenant, customer.Id.Value, 1, Buyer(), [], TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.cuc_taken", failure.Code);
    }

    [Fact]
    public async Task CorrectCityMovesTheCustomerAndReportsTheNewVersion()
    {
        var customer = NewComplete();
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CorrectCityAsync(
            Tenant, customer.Id.Value, 1, CityId, ValleCityId, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.Applied, 2, null, ValleCityId), outcome);
        Assert.Equal(ValleCityId, customer.CityId);
        Assert.Equal(ValleCityId, Assert.Single(customer.Addresses).CityId);
        Assert.Equal("CLI08000001", customer.Cuc);
        // P12: la auditoría de la corrección la escribe Shopify, una sola vez.
        Assert.Empty(bed.Audit.ByIntegration);
        Assert.Empty(bed.Audit.Human);
    }

    [Fact]
    public async Task CorrectCityTwiceIsAlreadyApplied()
    {
        var customer = NewComplete();
        var bed = NewBed(customer);

        await bed.Provisioning.CorrectCityAsync(Tenant, customer.Id.Value, 1, CityId, ValleCityId, [], TestContext.Current.CancellationToken);
        var updatedAt = customer.UpdatedAt;
        var second = await bed.Provisioning.CorrectCityAsync(
            Tenant, customer.Id.Value, 1, CityId, ValleCityId, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.AlreadyApplied, 2, null, ValleCityId), second);
        AssertUntouched(customer, 2, updatedAt, bed);
    }

    // El intento anterior lo llevó a la ciudad que el sinónimo tenía entonces; el reintento pide otra. Es la misma escritura
    // (versión siguiente, la ciudad ya no es la anterior): AlreadyApplied con la ciudad que quedó, sin tocar nada.
    [Fact]
    public async Task CorrectCityRetriedWithAnotherCityIsAlreadyAppliedWithTheWrittenCity()
    {
        var customer = NewComplete();
        var bed = NewBed(customer);
        await bed.Provisioning.CorrectCityAsync(Tenant, customer.Id.Value, 1, CityId, ValleCityId, [], TestContext.Current.CancellationToken);
        var updatedAt = customer.UpdatedAt;

        var retried = await bed.Provisioning.CorrectCityAsync(
            Tenant, customer.Id.Value, 1, CityId, Guid.NewGuid(), [ValleCityId], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.AlreadyApplied, 2, null, ValleCityId), retried);
        Assert.Equal(ValleCityId, customer.CityId);
        AssertUntouched(customer, 2, updatedAt, bed);
    }

    // Una persona le cambió la ciudad una vez después de la huella (versión siguiente, ya fuera de la anterior): esa ciudad no la
    // pidió este trabajo, así que no es un reintento suyo. Edited, y Shopify no la guarda ni la recorrige.
    [Fact]
    public async Task CorrectCityAfterAPersonChangedTheCityOnceIsEdited()
    {
        var customer = NewComplete();
        var personCity = Guid.NewGuid();
        customer.CorrectCity(CityId, personCity, Now.AddMinutes(1));
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CorrectCityAsync(
            Tenant, customer.Id.Value, 1, CityId, ValleCityId, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Edited), outcome);
        Assert.Equal(personCity, customer.CityId);
        AssertUntouched(customer, 2, updatedAt, bed);
    }

    [Fact]
    public async Task CorrectCityOfAnUnknownCustomerIsNotFound()
    {
        var bed = NewBed();

        var outcome = await bed.Provisioning.CorrectCityAsync(
            Tenant, Guid.NewGuid(), 1, CityId, ValleCityId, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotFound, null, null), outcome);
    }

    [Fact]
    public async Task CorrectCityOfAnEditedCustomerIsNotEligible()
    {
        var customer = NewComplete();
        customer.Deactivate(Now.AddMinutes(1));
        customer.Activate(Now.AddMinutes(2));
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CorrectCityAsync(
            Tenant, customer.Id.Value, 1, CityId, ValleCityId, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Edited), outcome);
        Assert.Equal(CityId, customer.CityId);
        AssertUntouched(customer, 3, updatedAt, bed);
    }

    [Fact]
    public async Task CorrectCityOfAnInactiveCustomerIsNotEligible()
    {
        var customer = NewComplete();
        customer.Deactivate(Now.AddMinutes(1));
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CorrectCityAsync(
            Tenant, customer.Id.Value, 2, CityId, ValleCityId, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Inactive), outcome);
        Assert.Equal(CityId, customer.CityId);
        AssertUntouched(customer, 2, updatedAt, bed);
    }

    [Fact]
    public async Task CorrectCityOfAMergedCustomerIsNotEligible()
    {
        var customer = NewComplete();
        CustomerMergePlan.Create(NewComplete(), [customer]).MarkMerged(Now.AddMinutes(1));
        var version = customer.Version;
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CorrectCityAsync(
            Tenant, customer.Id.Value, version, CityId, ValleCityId, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Merged), outcome);
        Assert.Equal(CityId, customer.CityId);
        AssertUntouched(customer, version, updatedAt, bed);
    }

    [Fact]
    public async Task CorrectCityOfAnIncompleteCustomerIsNotEligible()
    {
        var customer = NewIncomplete();
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CorrectCityAsync(
            Tenant, customer.Id.Value, 1, CityId, ValleCityId, [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.Incomplete), outcome);
        Assert.Null(customer.CityId);
        AssertUntouched(customer, 1, updatedAt, bed);
    }

    [Fact]
    public async Task CorrectCityOfACustomerThatMovedToAnotherCityIsNotEligible()
    {
        var customer = NewComplete(ValleCityId);
        var updatedAt = customer.UpdatedAt;
        var bed = NewBed(customer);

        var outcome = await bed.Provisioning.CorrectCityAsync(
            Tenant, customer.Id.Value, 1, CityId, Guid.NewGuid(), [], TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.CityChanged), outcome);
        Assert.Equal(ValleCityId, customer.CityId);
        AssertUntouched(customer, 1, updatedAt, bed);
    }
}
