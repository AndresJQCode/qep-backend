using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>CD-01 sin base: el 422 de documento repetido (<c>identification_taken</c>) lleva al dueño en <c>existing</c>.
/// El índice único es el árbitro, así que el dueño se relee después del <c>SaveChanges</c> que falla, nunca antes.</summary>
public sealed class CustomerWriteHandlersTests
{
    private const string TakenCode = "customers.customer.identification_taken";

    private static readonly EngagementTestBed.Context Manager =
        new(Tenant, EngagementTestBed.UserA, EngagementTestBed.Manager);

    private static Customer Owner(bool active = true)
    {
        var owner = Customer.CreateFromRegister(
            CustomerId.New(), Tenant, "Laura Pérez", new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            "3109352187", Now, new PlusNormalizer());
        if (!active)
        {
            owner.Deactivate(Now);
        }

        return owner;
    }

    private static Customer CompleteCustomer() =>
        Customer.Create(
            CustomerId.New(), Tenant, "CLI08000001", "Verde Esencial", null, null,
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900123456" },
            null,
            new CustomerContactInfo { Phone = "3000000000", Email = "v@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now,
            new PlusNormalizer());

    private static Customer IncompleteCustomer() =>
        Customer.CreateIncomplete(CustomerId.New(), Tenant, "Alguien", "3000000001", "CO", "bsuid-" + Guid.NewGuid().ToString("N"), Now, new PlusNormalizer());

    private static CreateCustomerHandler NewCreate(FakeCustomerRepository customers, UnitOfWork unitOfWork) =>
        new(customers, new Classifications([]), new EngagementTestBed.FakeTags(), new Geography([]), unitOfWork, new RecordingAudit(),
            new Cucs([]), Manager, new FixedClock(), new CreateCustomerValidator(), new PlusNormalizer());

    private static UpdateCustomerHandler NewUpdate(FakeCustomerRepository customers, UnitOfWork unitOfWork) =>
        new(customers, new Classifications([]), new EngagementTestBed.FakeTags(), new Geography([]), unitOfWork, new RecordingAudit(),
            new Cucs([]), Manager, new FixedClock(), new UpdateCustomerValidator(), new PlusNormalizer());

    private static CreateCustomerCommand CreateCommand() =>
        new(Tenant, "Laura Pérez", null, "CC", "1020304050", "310 935 2187", "laura@verde.co", "Calle 10 # 45-12", "CO", CityId,
            null, Classification.Id.Value, false, false);

    private static UpdateCustomerCommand UpdateCommand(Customer target) =>
        new(Tenant, target.Id.Value, "Laura Pérez", null, "CC", "1020304050", "310 935 2187", "laura@verde.co", "Calle 10 # 45-12", "CO",
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
}
