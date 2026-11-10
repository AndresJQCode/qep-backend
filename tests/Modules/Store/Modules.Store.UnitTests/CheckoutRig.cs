using Microsoft.Extensions.Logging;
using Modules.Store.Application;
using Modules.Store.Domain;
using static Modules.Store.UnitTests.StoreTestData;

namespace Modules.Store.UnitTests;

// Dobles del checkout: el manejador real sobre puertos a mano. Cada doble anota en el CallLog compartido lo que recibe, para afirmar
// el orden (nada se escribe antes de que todo lo que puede rechazar haya pasado).

internal sealed class FakeGeography : IStoreGeography
{
    public Task<IReadOnlyList<StoreGeoItem>> DepartmentsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<StoreGeoItem>?> CitiesAsync(Guid departmentId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StoreCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) =>
        Task.FromResult(cityId == CheckoutRig.CityId
            ? new StoreCityRef(CheckoutRig.CityId, "Medellín", CheckoutRig.DepartmentId, "Antioquia")
            : null);
}

/// <summary>Colombia antepone +57 a los dígitos, otro país sólo +; <c>12345</c> no normaliza en ninguno.</summary>
internal sealed class FakePhones : IStorePhoneNormalizer
{
    public string? ToE164(string phone, string country)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits == "12345")
        {
            return null;
        }

        return country == "CO" ? "+57" + digits : "+" + digits;
    }
}

internal sealed class FakeCaptcha(CallLog log) : ICaptchaVerifier
{
    public bool Passes { get; set; } = true;

    public int Calls { get; private set; }

    public Task<bool> VerifyAsync(string token, string remoteIp, string expectedHostname, CancellationToken cancellationToken)
    {
        Calls++;
        log.Entries.Add("captcha");
        return Task.FromResult(Passes);
    }
}

/// <summary>El motor de pedidos: cotiza fijo, cuenta y crea lo sembrado, y guarda cada borrador.</summary>
internal sealed class FakeOrders(CallLog log) : IStoreOrders
{
    public static readonly Guid OrderId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    public StoreOrderCreated? Existing { get; set; }

    public int Count { get; set; }

    public bool AlreadyExisted { get; set; }

    public Exception? CreateThrows { get; set; }

    public List<StoreOrderDraft> Drafts { get; } = [];

    public Task<StoreQuoteView> QuoteAsync(
        Guid tenantId,
        string currency,
        StorePricingMode mode,
        IReadOnlyList<(Guid VariantId, int Quantity)> lines,
        CancellationToken cancellationToken)
    {
        log.Entries.Add("orders.quote");
        var quoted = lines
            .Select(line => new StoreQuoteLineView(
                line.VariantId, CheckoutRig.ProductId, "Camiseta - M", line.Quantity, 10_000m, 0m, 10_000m, 10_000m * line.Quantity))
            .ToList();
        return Task.FromResult(new StoreQuoteView(currency, quoted, 20_000m, 0m, 0m, 20_000m));
    }

    public Task<StoreOrderCreated?> FindExistingAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken)
    {
        log.Entries.Add("orders.find");
        return Task.FromResult(Existing);
    }

    public Task<StoreOrderCreated> CreateAsync(StoreOrderDraft draft, CancellationToken cancellationToken)
    {
        log.Entries.Add("orders.create");
        if (CreateThrows is not null)
        {
            throw CreateThrows;
        }

        Drafts.Add(draft);
        return Task.FromResult(new StoreOrderCreated(
            OrderId, "PED-0001", 21_000m, "COP", draft.ClientId, draft.AdvisorMembershipId, AlreadyExisted));
    }

    public Task<int> CountCreatedSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        log.Entries.Add("orders.count");
        return Task.FromResult(Count);
    }
}

/// <summary>El directorio de clientes sembrado: qué devuelve cada camino y cada llamada que recibió.</summary>
internal sealed class FakeCustomers(CallLog log) : IStoreCustomerDirectory
{
    public static readonly Guid CreatedId = Guid.Parse("c1111111-1111-1111-1111-111111111111");

    public static readonly Guid IncompleteId = Guid.Parse("c2222222-2222-2222-2222-222222222222");

    public StoreCustomerRef? ByDocument { get; set; }

    public StoreCustomerCreationOutcome CreateOutcome { get; set; } = StoreCustomerCreationOutcome.Created;

    /// <summary>Lo que devuelve el alta con <c>Created</c> o <c>ExistingByDocument</c>.</summary>
    public StoreCustomerRef CreatedCustomer { get; set; } = new(CreatedId, true, false, false);

    public StoreCustomerRef CreatedIncomplete { get; set; } = new(IncompleteId, false, false, false);

    /// <summary>Lo que devuelve el completado; null = el mismo cliente, ya completo.</summary>
    public StoreCustomerRef? Completed { get; set; }

    /// <summary>La salida de cada dirección; vacía = <c>Added</c> para todas.</summary>
    public List<StoreAddressOutcome> AddressOutcomes { get; } = [];

    public List<(Guid TenantId, Guid? ActorUserId, StoreNewCustomer Data)> CreateCalls { get; } = [];

    public List<(Guid? ActorUserId, Guid CustomerId, StoreNewCustomer Billing)> CompleteCalls { get; } = [];

    public List<(string Name, string? Phone, string? Email, string? Country)> IncompleteCalls { get; } = [];

    public List<IReadOnlyList<StoreAddressInput>> AddressCalls { get; } = [];

    public Task<StoreCustomerRef?> FindByDocumentAsync(
        Guid tenantId, string identificationType, string identificationNumber, CancellationToken cancellationToken)
    {
        log.Entries.Add("customers.find");
        return Task.FromResult(ByDocument);
    }

    public Task<StoreCustomerCreation> CreateAsync(
        Guid tenantId, Guid? actorUserId, StoreNewCustomer data, CancellationToken cancellationToken)
    {
        log.Entries.Add("customers.create");
        CreateCalls.Add((tenantId, actorUserId, data));
        var customer = CreateOutcome is StoreCustomerCreationOutcome.Created or StoreCustomerCreationOutcome.ExistingByDocument
            ? CreatedCustomer
            : null;
        return Task.FromResult(new StoreCustomerCreation(customer, CreateOutcome));
    }

    public Task<StoreCustomerRef> CreateIncompleteAsync(
        Guid tenantId, string name, string? phone, string? email, string? country, CancellationToken cancellationToken)
    {
        log.Entries.Add("customers.incomplete");
        IncompleteCalls.Add((name, phone, email, country));
        return Task.FromResult(CreatedIncomplete);
    }

    public Task<StoreCustomerRef> CompleteAsync(
        Guid tenantId, Guid? actorUserId, Guid customerId, StoreNewCustomer billing, CancellationToken cancellationToken)
    {
        log.Entries.Add("customers.complete");
        CompleteCalls.Add((actorUserId, customerId, billing));
        return Task.FromResult(Completed ?? new StoreCustomerRef(customerId, true, false, false));
    }

    public Task<IReadOnlyList<StoreAddressOutcome>> EnsureAddressesAsync(
        Guid tenantId, Guid customerId, IReadOnlyList<StoreAddressInput> addresses, CancellationToken cancellationToken)
    {
        log.Entries.Add("customers.addresses");
        AddressCalls.Add(addresses);
        IReadOnlyList<StoreAddressOutcome> outcomes = AddressOutcomes.Count > 0
            ? addresses.Select((_, index) => AddressOutcomes[Math.Min(index, AddressOutcomes.Count - 1)]).ToList()
            : addresses.Select(_ => StoreAddressOutcome.Added).ToList();
        return Task.FromResult(outcomes);
    }

    public Task<IReadOnlyList<StoreCustomerCandidate>> FindCandidatesAsync(
        Guid tenantId, string phoneE164, string? identificationType, string? identificationNumber, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlySet<int>> KnownAsync(
        Guid tenantId, IReadOnlyCollection<(string PhoneE164, string? Type, string? Number)> keys, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StoreCustomerRef?> FindEligibleAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeConsents(CallLog log) : IStoreOrderConsents
{
    public List<StoreOrderConsent> Added { get; } = [];

    public void Add(StoreOrderConsent consent)
    {
        log.Entries.Add("consents.add");
        Added.Add(consent);
    }

    public Task<StoreOrderConsent?> FindAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(Added.FirstOrDefault(consent => consent.OrderId == orderId));
}

internal sealed class FakeOutbox(CallLog log) : IStoreOutbox
{
    public List<(string EventName, object Payload)> Messages { get; } = [];

    public void Add(string eventName, object payload, DateTimeOffset occurredAt)
    {
        log.Entries.Add("outbox.add");
        Messages.Add((eventName, payload));
    }
}

internal sealed class FakeBuyers : IStoreBuyerAccounts
{
    public string HashPassword(string password) => "hash";

    public Task<bool> ReplaceProfileAsync(
        string host, string? sessionToken, PublicBillingInput billing, PublicShippingInput? shipping, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

/// <summary>Guarda en memoria lo que se loguea, para afirmar el nivel y que el mensaje no lleve datos del visitante.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<string> At(LogLevel level) =>
        Entries.Where(entry => entry.Level == level).Select(entry => entry.Message);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>El manejador del checkout armado sobre dobles, con una tienda publicada en <c>acme.lulocrm.com</c>.</summary>
internal sealed class CheckoutRig
{
    public const string Host = "acme.lulocrm.com";

    public const string VisitorIp = "198.51.100.23";

    public static readonly Guid DepartmentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public static readonly Guid CityId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public static readonly Guid VariantId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    public static readonly Guid ProductId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    public CheckoutRig(OnlineStore? store = null)
    {
        Store = store ?? ConfiguredStore();
        Store.Publish(Member, Now);
        Domains = new FakeDomains(Log) { Seeded = StoreDomain.CreateSubdomain(TenantId, "acme", "lulocrm.com", Now) };
        Stores = new FakeStores(Log, Store);
        Catalog.Sellable[VariantId] = new StoreSellableVariant(VariantId, ProductId, "Camiseta - M", null);
        Captcha = new FakeCaptcha(Log);
        Orders = new FakeOrders(Log);
        Customers = new FakeCustomers(Log);
        Consents = new FakeConsents(Log);
        Audit = new FakeAudit(Log);
        Outbox = new FakeOutbox(Log);
        UnitOfWork = new FakeUnitOfWork(Log);
        Members.Owner = OtherMember;
    }

    public CallLog Log { get; } = new();

    public OnlineStore Store { get; }

    public FakeDomains Domains { get; }

    public FakeStores Stores { get; }

    public FakeCatalog Catalog { get; } = new();

    public FakeCaptcha Captcha { get; }

    public FakeOrders Orders { get; }

    public FakeMembers Members { get; } = new();

    public FakeCustomers Customers { get; }

    public FakeConsents Consents { get; }

    public FakeAudit Audit { get; }

    public FakeOutbox Outbox { get; }

    public FakeUnitOfWork UnitOfWork { get; }

    public StoreOptions Options { get; } = new() { RootDomain = "lulocrm.com" };

    public RecordingLogger<SubmitOrderRequestHandler> Logger { get; } = new();

    public SubmitOrderRequestHandler Handler() =>
        new(
            new StorefrontResolver(Domains, Stores, new FakeTenants(), FakeTenantModules.AllEnabled()),
            new FakeGeography(),
            new FakePhones(),
            Captcha,
            new PublicCartPricing(Catalog, Orders),
            Orders,
            Members,
            Customers,
            Consents,
            UnitOfWork,
            Audit,
            new FixedClock(Now),
            Options,
            new PublicOrderRequestBodyValidator(),
            new FakeBuyers(),
            Outbox,
            Logger);

    public Task<(PublicOrderRequestReceiptDto Receipt, bool Created)> SubmitAsync(PublicOrderRequestBody body) =>
        Handler().HandleAsync(new SubmitOrderRequestCommand(Host, VisitorIp, body), TestContext.Current.CancellationToken);

    public static PublicBillingInput Billing() =>
        new("Ana Pérez", "CC", "123456", "300 123 4567", "ana@test.co", "Calle 1 # 2-3", "CO", DepartmentId, CityId, null);

    public static PublicShippingInput Shipping() =>
        new("Luis Gómez", "300 765 4321", "Carrera 9 # 8-7", "CO", DepartmentId, CityId, null);

    public static PublicOrderRequestBody Body(
        string? currency = "COP",
        PublicShippingInput? shipping = null,
        PublicBuyerSignupInput? createAccount = null) =>
        new(
            Guid.Parse("f0000000-0000-0000-0000-000000000001"),
            "turnstile-token",
            currency,
            Billing(),
            shipping,
            "Entregar en portería",
            true,
            [new PublicCartLineRequest(VariantId, 2)],
            createAccount);
}
