using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>CD-01 sin base: el 422 de documento repetido (<c>identification_taken</c>) lleva al dueño en <c>existing</c>.
/// El índice único es el árbitro, así que el dueño se relee después del <c>SaveChanges</c> que falla, nunca antes.</summary>
public sealed class CustomerWriteHandlersTests
{
    private const string TakenCode = "customers.customer.identification_taken";

    private static readonly EngagementTestBed.Context Manager =
        new(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, EngagementTestBed.Manager);

    private static Customer Owner(bool active = true)
    {
        var owner = Customer.CreateFromRegister(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "Laura Pérez", new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            "3109352187", Now, new PlusNormalizer());
        if (!active)
        {
            owner.Deactivate(Now);
        }

        return owner;
    }

    private static Customer CompleteCustomer() =>
        Customer.Create(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "CLI08000001", "Verde Esencial", null, null,
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900123456" },
            null,
            new CustomerContactInfo { Phone = "3000000000", Email = "v@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now,
            new PlusNormalizer());

    private static Customer IncompleteCustomer() =>
        Customer.CreateIncomplete(CustomerId.New(), CustomerWriteDoubles.Tenant, "Alguien", "3000000001", "CO", "bsuid-" + Guid.NewGuid().ToString("N"), Now, new PlusNormalizer());

    private static CreateCustomerHandler NewCreate(
        FakeCustomerRepository customers, UnitOfWork unitOfWork, TenantDirectory? tenant = null, List<string>? calls = null) =>
        new(customers, new Classifications([]), new EngagementTestBed.FakeTags(), new Geography([]), unitOfWork, new RecordingAudit(),
            new Cucs(calls ?? []), Manager, new FixedClock(), new CreateCustomerValidator(), new PlusNormalizer(),
            tenant ?? new TenantDirectory());

    private static UpdateCustomerHandler NewUpdate(
        FakeCustomerRepository customers, UnitOfWork unitOfWork, TenantDirectory? tenant = null, List<string>? calls = null) =>
        new(customers, new Classifications([]), new EngagementTestBed.FakeTags(), new Geography([]), unitOfWork, new RecordingAudit(),
            new Cucs(calls ?? []), Manager, new FixedClock(), new UpdateCustomerValidator(), new PlusNormalizer(),
            tenant ?? new TenantDirectory());

    private static CreateCustomerCommand CreateCommand() =>
        new(CustomerWriteDoubles.Tenant, "Laura Pérez", null, "CC", "1020304050", "310 935 2187", "laura@verde.co", "Calle 10 # 45-12", "CO", CityId,
            null, Classification.Id.Value, false, false);

    private static UpdateCustomerCommand UpdateCommand(Customer target) =>
        new(CustomerWriteDoubles.Tenant, target.Id.Value, "Laura Pérez", null, "CC", "1020304050", "310 935 2187", "laura@verde.co", "Calle 10 # 45-12", "CO",
            CityId, null, Classification.Id.Value, false, false);

    private static UnitOfWork Taken() => new(new CustomersDomainException(TakenCode, "taken"));

    private static void AssertOwner(Customer owner, CustomerIdentificationTakenException failure) =>
        Assert.Equal(
            new CustomerIdentificationOwnerDto(owner.Id.Value, owner.Name, "CC", "1020304050", null, false, owner.IsActive),
            failure.ProblemExtensions["existing"]);

    [Fact]
    public async Task CreateWithATakenDocumentThrowsWithTheOwner()
    {
        var owner = Owner(active: false);
        var customers = new FakeCustomerRepository { Existing = owner };

        var failure = await Assert.ThrowsAsync<CustomerIdentificationTakenException>(() =>
            NewCreate(customers, Taken()).HandleAsync(CreateCommand(), TestContext.Current.CancellationToken));

        Assert.Equal(TakenCode, failure.Code);
        Assert.Equal(
            new CustomerIdentificationOwnerDto(owner.Id.Value, "Laura Pérez", "CC", "1020304050", null, false, false),
            failure.ProblemExtensions["existing"]);
        Assert.Equal(1, customers.IdentificationLookups);
    }

    [Fact]
    public async Task UpdateAndCompleteWithATakenDocumentThrowWithTheOwner()
    {
        var owner = Owner();

        // Camino de editar (ficha completa).
        var complete = CompleteCustomer();
        var editing = new FakeCustomerRepository { Existing = owner };
        editing.Known.Add(complete);
        var editFailure = await Assert.ThrowsAsync<CustomerIdentificationTakenException>(() =>
            NewUpdate(editing, Taken()).HandleAsync(UpdateCommand(complete), TestContext.Current.CancellationToken));
        AssertOwner(owner, editFailure);

        // Camino de completar (ficha incompleta).
        var incomplete = IncompleteCustomer();
        var completing = new FakeCustomerRepository { Existing = owner };
        completing.Known.Add(incomplete);
        var completeFailure = await Assert.ThrowsAsync<CustomerIdentificationTakenException>(() =>
            NewUpdate(completing, Taken()).HandleAsync(UpdateCommand(incomplete), TestContext.Current.CancellationToken));
        AssertOwner(owner, completeFailure);
        Assert.Equal(TakenCode, completeFailure.Code);
    }

    [Fact]
    public async Task WhenTheOwnerCannotBeReadBackTheOriginal422GoesOutWithoutExisting()
    {
        var customers = new FakeCustomerRepository();

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            NewCreate(customers, Taken()).HandleAsync(CreateCommand(), TestContext.Current.CancellationToken));

        Assert.Equal(TakenCode, failure.Code);
        Assert.IsNotType<CustomerIdentificationTakenException>(failure);
    }

    [Fact]
    public async Task AnUpdateThatSavesNeverLooksUpTheIdentification()
    {
        var complete = CompleteCustomer();
        var incomplete = IncompleteCustomer();
        var customers = new FakeCustomerRepository();
        customers.Known.Add(complete);
        customers.Known.Add(incomplete);
        var handler = NewUpdate(customers, new UnitOfWork());

        // Los dos caminos terminan antes de armar la respuesta (el doble de ciudades no resuelve la libreta): lo que
        // importa es que el guardado no pidió al dueño. Un fallo posterior al guardado no cambia el contador.
        foreach (var target in new[] { complete, incomplete })
        {
            try
            {
                await handler.HandleAsync(UpdateCommand(target), TestContext.Current.CancellationToken);
            }
            catch (NotSupportedException)
            {
                // El doble de geografía no implementa FindCitiesAsync; es posterior al SaveChanges.
            }
        }

        Assert.Equal(0, customers.IdentificationLookups);
    }

    // Spec 2026-10-26: el doble de geografia no resuelve la libreta, asi que la respuesta no se puede armar y estas pruebas
    // miran la entidad que el handler dejo guardada, no el DTO.
    [Fact]
    public async Task CreatePassesThePersonTypeToTheCustomer()
    {
        var customers = new FakeCustomerRepository();
        var command = CreateCommand() with { IdentificationType = "NIT", IdentificationNumber = "900123456", PersonType = "Juridica" };

        try
        {
            await NewCreate(customers, new UnitOfWork()).HandleAsync(command, TestContext.Current.CancellationToken);
        }
        catch (NotSupportedException)
        {
            // El doble de geografia no implementa FindCitiesAsync; es posterior al SaveChanges.
        }

        Assert.Equal(PersonType.Juridica, Assert.Single(customers.Added).PersonType);
    }

    [Fact]
    public async Task UpdateWithoutPersonTypeClearsIt()
    {
        var customers = new FakeCustomerRepository();
        var target = CompleteCustomer();
        customers.Known.Add(target);
        target.Update(
            target.Name, null, new CustomerIdentification { Type = IdentificationType.Nit, Number = "900123456" }, PersonType.Juridica,
            new CustomerContactInfo { Phone = "3000000000", Email = "v@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id }, null, Now, new PlusNormalizer());
        Assert.Equal(PersonType.Juridica, target.PersonType);

        try
        {
            await NewUpdate(customers, new UnitOfWork()).HandleAsync(UpdateCommand(target), TestContext.Current.CancellationToken);
        }
        catch (NotSupportedException)
        {
            // Posterior al SaveChanges.
        }

        Assert.Null(target.PersonType);
    }

    [Fact]
    public async Task AnUnknownPersonTypeIsAValidationError()
    {
        var command = CreateCommand() with { PersonType = "Empresa" };

        var failure = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            NewCreate(new FakeCustomerRepository(), new UnitOfWork()).HandleAsync(command, TestContext.Current.CancellationToken));

        Assert.Contains(failure.Errors, error => error.PropertyName == "PersonType");
    }

    // Spec 2026-10-26, Tarea 8: con OnCustomerSave el tipo de persona es obligatorio al guardar un cliente colombiano de un
    // tenant colombiano. El doble de geografia no resuelve la libreta, asi que las pruebas que guardan miran la entidad.
    private static TenantDirectory OnSave(string tenantCountry = "CO") =>
        TenantDirectory.With(tenantCountry, PersonTypeRequirement.OnCustomerSave);

    private static async Task SaveIgnoringTheResponseAsync(Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (NotSupportedException)
        {
            // El doble de geografia no implementa FindCitiesAsync; es posterior al SaveChanges.
        }
    }

    [Fact]
    public async Task OnCustomerSaveRejectsAColombianCreateWithoutPersonType()
    {
        var customers = new FakeCustomerRepository();
        var calls = new List<string>();

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            NewCreate(customers, new UnitOfWork(), OnSave(), calls).HandleAsync(CreateCommand(), TestContext.Current.CancellationToken));

        Assert.Equal(CustomersErrorCodes.PersonTypeRequired, failure.Code);
        Assert.Equal(["PersonType"], failure.FieldErrors.Keys);
        Assert.Empty(customers.Added);
        Assert.DoesNotContain("cuc", calls);
    }

    [Fact]
    public async Task OnCustomerSaveRejectsAnUpdateAndACompletionWithoutPersonType()
    {
        var complete = CompleteCustomer();
        var incomplete = IncompleteCustomer();
        var customers = new FakeCustomerRepository();
        customers.Known.Add(complete);
        customers.Known.Add(incomplete);
        var calls = new List<string>();

        foreach (var target in new[] { complete, incomplete })
        {
            var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
                NewUpdate(customers, new UnitOfWork(), OnSave(), calls).HandleAsync(UpdateCommand(target), TestContext.Current.CancellationToken));

            Assert.Equal(CustomersErrorCodes.PersonTypeRequired, failure.Code);
            Assert.Equal(["PersonType"], failure.FieldErrors.Keys);
        }

        // El incompleto sigue incompleto y no quemo un numero del consecutivo.
        Assert.False(incomplete.IsComplete);
        Assert.DoesNotContain("cuc", calls);
    }

    [Fact]
    public async Task OnCustomerSaveAcceptsAPersonType()
    {
        var customers = new FakeCustomerRepository();
        var command = CreateCommand() with { IdentificationType = "NIT", IdentificationNumber = "900123456", PersonType = "Juridica" };

        await SaveIgnoringTheResponseAsync(() =>
            NewCreate(customers, new UnitOfWork(), OnSave()).HandleAsync(command, TestContext.Current.CancellationToken));

        Assert.Equal(PersonType.Juridica, Assert.Single(customers.Added).PersonType);
    }

    [Theory]
    [InlineData(PersonTypeRequirement.Never)]
    [InlineData(PersonTypeRequirement.OnOrderApproval)]
    public async Task NeverAndOnOrderApprovalDoNotRequireIt(PersonTypeRequirement requirement)
    {
        var customers = new FakeCustomerRepository();

        await SaveIgnoringTheResponseAsync(() =>
            NewCreate(customers, new UnitOfWork(), TenantDirectory.With("CO", requirement)).HandleAsync(CreateCommand(), TestContext.Current.CancellationToken));

        Assert.Null(Assert.Single(customers.Added).PersonType);
    }

    [Fact]
    public async Task AForeignCustomerOfAColombianTenantSavesWithoutPersonType()
    {
        var customers = new FakeCustomerRepository();
        var command = CreateCommand() with { Country = "ES", CityId = null, CityName = "Madrid" };

        await SaveIgnoringTheResponseAsync(() =>
            NewCreate(customers, new UnitOfWork(), OnSave()).HandleAsync(command, TestContext.Current.CancellationToken));

        Assert.Null(Assert.Single(customers.Added).PersonType);
    }

    [Fact]
    public async Task ANonColombianTenantWithOnCustomerSaveCreatesWithoutPersonType()
    {
        var customers = new FakeCustomerRepository();

        await SaveIgnoringTheResponseAsync(() =>
            NewCreate(customers, new UnitOfWork(), OnSave("ES")).HandleAsync(CreateCommand(), TestContext.Current.CancellationToken));

        Assert.Null(Assert.Single(customers.Added).PersonType);
    }

    // La dirección propia del cliente es opcional. Sin calle no se siembra la libreta, cuya fila sí la exige
    // (customers.address.address_required): nace sin ella, igual que un cliente de afuera.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateWithoutAnAddressIsCompleteWithNoAddressAndAnEmptyBook(string? address)
    {
        var customers = new FakeCustomerRepository();
        var command = CreateCommand() with { Address = address };

        await SaveIgnoringTheResponseAsync(() =>
            NewCreate(customers, new UnitOfWork()).HandleAsync(command, TestContext.Current.CancellationToken));

        var customer = Assert.Single(customers.Added);
        Assert.Null(customer.Address);
        Assert.Empty(customer.Addresses);
        Assert.True(customer.IsComplete);
    }

    [Fact]
    public async Task CreateWithAnAddressStillSeedsTheBook()
    {
        var customers = new FakeCustomerRepository();

        await SaveIgnoringTheResponseAsync(() =>
            NewCreate(customers, new UnitOfWork()).HandleAsync(CreateCommand(), TestContext.Current.CancellationToken));

        var customer = Assert.Single(customers.Added);
        Assert.Equal("Calle 10 # 45-12", customer.Address);
        Assert.Equal("Calle 10 # 45-12", Assert.Single(customer.Addresses).Address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompletingWithoutAnAddressIsCompleteWithNoAddressAndAnEmptyBook(string? address)
    {
        var customers = new FakeCustomerRepository();
        var incomplete = IncompleteCustomer();
        customers.Known.Add(incomplete);

        await SaveIgnoringTheResponseAsync(() =>
            NewUpdate(customers, new UnitOfWork()).HandleAsync(
                UpdateCommand(incomplete) with { Address = address }, TestContext.Current.CancellationToken));

        Assert.True(incomplete.IsComplete);
        Assert.Null(incomplete.Address);
        Assert.Empty(incomplete.Addresses);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateClearingTheAddressSavesNullAndLeavesTheBookUntouched(string? address)
    {
        var customers = new FakeCustomerRepository();
        var target = Customer.Create(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "CLI08000001", "Verde Esencial", null,
            new CustomerAddressDetails { Name = "Bodega", Address = "Carrera 7 # 71-21", CityId = CityId },
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900123456" },
            null,
            new CustomerContactInfo { Phone = "3000000000", Email = "v@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now,
            new PlusNormalizer());
        customers.Known.Add(target);
        var row = Assert.Single(target.Addresses);

        await SaveIgnoringTheResponseAsync(() =>
            NewUpdate(customers, new UnitOfWork()).HandleAsync(
                UpdateCommand(target) with { Address = address }, TestContext.Current.CancellationToken));

        Assert.Null(target.Address);
        Assert.Same(row, Assert.Single(target.Addresses));
        Assert.Equal("Carrera 7 # 71-21", row.Address);
    }

    [Fact]
    public async Task AnAddressLongerThanTheColumnIsStillRejectedWithTheFieldError()
    {
        var tooLong = new string('d', CustomerContactInfo.AddressMaxLength + 1);
        var complete = CompleteCustomer();
        var customers = new FakeCustomerRepository();
        customers.Known.Add(complete);

        var onCreate = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            NewCreate(new FakeCustomerRepository(), new UnitOfWork()).HandleAsync(
                CreateCommand() with { Address = tooLong }, TestContext.Current.CancellationToken));
        var onUpdate = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            NewUpdate(customers, new UnitOfWork()).HandleAsync(
                UpdateCommand(complete) with { Address = tooLong }, TestContext.Current.CancellationToken));

        Assert.Contains(onCreate.Errors, error => error.PropertyName == "Address");
        Assert.Contains(onUpdate.Errors, error => error.PropertyName == "Address");
    }
}
