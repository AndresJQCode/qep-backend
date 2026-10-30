using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Pos.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Customers → Pos (spec 2026-10-14 «cliente identificado»). La caja ve el tipo de documento como la cadena del contrato
/// (<c>CC</c>, no el enum), un cliente de WhatsApp sin documento sale con los campos en null, y buscar por documento
/// devuelve también al dueño inactivo para que el handler diga «inactivo» en vez de intentar crearlo otra vez.
/// </summary>
public sealed class PosCustomerLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SearchMapsTheSelectableCustomersToRefsWithTheWireType()
    {
        var customer = Registered("Laura Verde", IdentificationType.Cc, "1020304050");
        var lookup = Lookup(new PosFakeCustomers(customer), new PosFakeProvisioning(), new PosFakeExecutionContext());

        var found = await lookup.SearchAsync(TenantId, "laura", PosCustomerRules.Take, Ct);

        var item = Assert.Single(found);
        Assert.Equal(new PosCustomerRef(customer.Id.Value, "Laura Verde", "CC", "1020304050", false, true), item);
    }

    [Fact]
    public async Task FindReturnsNullForAnotherTenantsCustomer()
    {
        var customer = Registered("Laura Verde", IdentificationType.Cc, "1020304050");
        var lookup = Lookup(new PosFakeCustomers(customer), new PosFakeProvisioning(), new PosFakeExecutionContext());

        Assert.Null(await lookup.FindAsync(Guid.CreateVersion7(), customer.Id.Value, Ct));
        Assert.NotNull(await lookup.FindAsync(TenantId, customer.Id.Value, Ct));
    }

    [Fact]
    public async Task FindReportsInactiveAndMissingDocument()
    {
        var whatsApp = Customer.CreateIncomplete(
            CustomerId.New(), TenantId, "Laura", "+573001234567", "CO", "wa-1", Now, new PassThroughPhones());
        var inactive = Registered("Pedro Gris", IdentificationType.Nit, "900123456-1");
        inactive.Deactivate(Now);
        var lookup = Lookup(new PosFakeCustomers(whatsApp, inactive), new PosFakeProvisioning(), new PosFakeExecutionContext());

        Assert.Equal(
            new PosCustomerSnapshot(whatsApp.Id.Value, "Laura", null, null, true),
            await lookup.FindAsync(TenantId, whatsApp.Id.Value, Ct));
        Assert.Equal(
            new PosCustomerSnapshot(inactive.Id.Value, "Pedro Gris", "NIT", "900123456-1", false),
            await lookup.FindAsync(TenantId, inactive.Id.Value, Ct));
    }

    [Fact]
    public async Task FindByIdentificationReturnsAnInactiveOwnerToo()
    {
        var inactive = Registered("Pedro Gris", IdentificationType.Cc, "1020304050");
        inactive.Deactivate(Now);
        var customers = new PosFakeCustomers(inactive)
        {
            Identifications = { [(IdentificationType.Cc, "1020304050")] = inactive.Id }
        };
        var lookup = Lookup(customers, new PosFakeProvisioning(), new PosFakeExecutionContext());

        var found = await lookup.FindByIdentificationAsync(TenantId, "cc", " 1020304050 ", Ct);

        Assert.Equal(new PosCustomerRef(inactive.Id.Value, "Pedro Gris", "CC", "1020304050", false, false), found);
        Assert.Null(await lookup.FindByIdentificationAsync(TenantId, "CC", "999", Ct));
    }

    [Fact]
    public async Task CreateFromRegisterPassesTheCashierAsActorAndReturnsTheNormalizedRef()
    {
        // El agregado recorta el número al armar la ficha; el ref que vuelve es el guardado, no lo tecleado.
        var stored = Registered("Laura Verde", IdentificationType.Cc, " 1020304050 ");
        var provisioning = new PosFakeProvisioning { Result = stored.Id.Value };
        var context = new PosFakeExecutionContext();
        var lookup = Lookup(new PosFakeCustomers(stored), provisioning, context);

        var created = await lookup.CreateFromRegisterAsync(
            TenantId, new NewPosCustomer("Laura Verde", "CC", " 1020304050 ", "3001234567"), Ct);

        Assert.Equal(new PosCustomerRef(stored.Id.Value, "Laura Verde", "CC", "1020304050", false, true), created);
        var call = Assert.Single(provisioning.Calls);
        Assert.Equal(TenantId, call.TenantId);
        Assert.Equal(context.SubjectId, call.ActorId);
        Assert.Equal("pos", call.Source);
        Assert.Equal(new RegisterCustomerData("Laura Verde", "CC", " 1020304050 ", "3001234567"), call.Data);
    }

    // Pos.Application no ve Customers y por eso copia la lista de tipos; esta prueba es lo que impide que las dos diverjan.
    [Fact]
    public void TheIdentificationTypesMatchTheCustomersModule()
    {
        Assert.True(
            PosCustomerRules.IdentificationTypes.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(IdentificationTypeParser.SupportedWireValues));
    }

    // Pos copia los anchos de nombre, documento y teléfono; si Customers los cambia, el alta rápida debe seguirlos.
    [Fact]
    public void TheLengthLimitsMatchTheCustomersModule()
    {
        Assert.Equal(Customer.NameMaxLength, PosCustomerRules.NameMaxLength);
        Assert.Equal(CustomerIdentification.NumberMaxLength, PosCustomerRules.IdentificationNumberMaxLength);
        Assert.Equal(CustomerContactInfo.PhoneMaxLength, PosCustomerRules.PhoneMaxLength);
    }

    private static PosCustomerLookup Lookup(
        PosFakeCustomers customers, PosFakeProvisioning provisioning, PosFakeExecutionContext context) =>
        new(customers, provisioning, context);

    private static Customer Registered(string name, IdentificationType type, string number) =>
        Customer.CreateFromRegister(
            CustomerId.New(), TenantId, name, new CustomerIdentification { Type = type, Number = number }, null, Now, new PassThroughPhones());

    private sealed class PosFakeExecutionContext : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId { get; } = new(PosCustomerLookupTests.TenantId);

        public bool HasPermission(string permission) => true;
    }

    private sealed class PosFakeProvisioning : ICustomerProvisioning
    {
        public Guid Result { get; init; }

        public List<(Guid TenantId, Guid ActorId, RegisterCustomerData Data, string? Source)> Calls { get; } = [];

        public Task<ProvisionedCustomer> CreateFromRegisterAsync(
            Guid tenantId, Guid actorId, RegisterCustomerData data, string? source, CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, actorId, data, source));
            return Task.FromResult(new ProvisionedCustomer(Result, false, true, true));
        }

        public Task<ProvisionedCustomer> CreateCompleteAsync(Guid tenantId, CompleteCustomerData data, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProvisionedCustomer> CreateIncompleteAsync(
            Guid tenantId, string? name, string? phone, string? email, string? country, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalCustomerCityOutcome> CompleteExternalSaleAsync(
            Guid tenantId,
            Guid customerId,
            long expectedVersion,
            CompleteCustomerData data,
            IReadOnlyCollection<Guid> requestedCityIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalCustomerCityOutcome> CorrectCityAsync(
            Guid tenantId,
            Guid customerId,
            long expectedVersion,
            Guid previousCityId,
            Guid cityId,
            IReadOnlyCollection<Guid> requestedCityIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PosFakeCustomers(params Customer[] customers) : ICustomerRepository
    {
        public Dictionary<(IdentificationType Type, string Number), CustomerId> Identifications { get; } = [];

        public Task<IReadOnlyList<Customer>> SearchSelectableAsync(
            Guid tenantId, string? search, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Customer>>(
                customers.Where(customer => customer.TenantId == tenantId && customer.IsActive).Take(take).ToList());

        public Task<Customer?> FindAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            Task.FromResult(customers.FirstOrDefault(customer => customer.TenantId == tenantId && customer.Id == customerId));

        public Task<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>> FindExistingIdentificationsAsync(
            Guid tenantId, IReadOnlyCollection<(IdentificationType Type, string Number)> identifications, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>>(
                Identifications.Where(pair => identifications.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

        public Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(
            Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, IReadOnlyCollection<Guid>? cityIds,
            bool? isComplete, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Customer>> ListForExportAsync(
            Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, int skip, int take,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> AnyWithClassificationAsync(
            Guid tenantId, ClientClassificationId classificationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, CustomerId>> FindIdsByCucSuffixAsync(
            Guid tenantId, IReadOnlyCollection<string> suffixes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationNumberAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> SearchIdsByCucAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<CustomerId, string>> FindNamesByIdsAsync(
            Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<CustomerId, Customer>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Customer?> FindByWhatsAppUserIdAsync(Guid tenantId, string whatsAppUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Customer?> FindByInstagramUserIdAsync(Guid tenantId, string instagramUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Customer?> FindOldestByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Customer?> FindOldestByUnnormalizedPhoneAsync(Guid tenantId, string phone, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryAttachWhatsAppUserIdAsync(Guid tenantId, CustomerId customerId, string whatsAppUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerWhatsAppRef>> FindWhatsAppRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerId?> FindIdByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(Customer customer) => throw new NotSupportedException();
    }
}
