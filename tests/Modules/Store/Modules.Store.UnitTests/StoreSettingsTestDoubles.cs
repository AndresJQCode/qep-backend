using BuildingBlocks.Application;
using Modules.Store.Application;
using Modules.Store.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using static Modules.Store.UnitTests.StoreTestData;

namespace Modules.Store.UnitTests;

// Dobles a mano de los puertos de Store.Application, sin librería de mocking, como el resto del repositorio: devuelven lo sembrado y
// anotan lo que reciben y en qué orden, que es lo que estas pruebas fijan.

internal sealed class FakeExecutionContext(Guid tenantId, params string[] permissions) : IExecutionContext
{
    public Guid SubjectId => Member;

    public Modules.Tenancy.Domain.TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission);
}

internal sealed class FakeTenantModules(TenantModuleSet? set) : ITenantModules
{
    public static FakeTenantModules AllEnabled() => new(TenantModuleSet.FromStored(TenantModuleKeys.All));

    public static FakeTenantModules StoreOff() =>
        new(TenantModuleSet.FromStored(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Store)));

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(set);
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>El registro compartido de lo que ocurrió y en qué orden: guardar y reemplazar el dominio tienen que ir juntos.</summary>
internal sealed class CallLog
{
    public List<string> Entries { get; } = [];
}

internal sealed class FakeStores(CallLog log, OnlineStore? seeded = null) : IOnlineStoreRepository
{
    public OnlineStore? Stored { get; private set; } = seeded;

    public HashSet<string> SlugsOfOtherTenants { get; } = [];

    public Task<OnlineStore?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(Stored);

    public void Add(OnlineStore store)
    {
        log.Entries.Add("stores.add");
        Stored = store;
    }

    public Task<bool> SlugTakenByAnotherAsync(Guid tenantId, string slug, CancellationToken cancellationToken) =>
        Task.FromResult(SlugsOfOtherTenants.Contains(slug));
}

internal sealed class FakeDomains(CallLog log) : IStoreDomainRepository
{
    public List<(string? Slug, string RootDomain)> Replaced { get; } = [];

    /// <summary>El dominio que resuelve la tienda pública; otro host no es de nadie.</summary>
    public StoreDomain? Seeded { get; init; }

    public Task<StoreDomain?> FindByHostAsync(string hostname, CancellationToken cancellationToken) =>
        Task.FromResult(Seeded is not null && Seeded.Hostname == hostname ? Seeded : null);

    public Task ReplaceSubdomainAsync(Guid tenantId, string? slug, string rootDomain, CancellationToken cancellationToken)
    {
        log.Entries.Add("domains.replace");
        Replaced.Add((slug, rootDomain));
        return Task.CompletedTask;
    }
}

internal sealed class FakeUnitOfWork(CallLog log) : IStoreUnitOfWork
{
    public int Saves { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        log.Entries.Add("uow.save");
        return Task.CompletedTask;
    }
}

internal sealed class FakeAudit(CallLog? log = null) : IStoreAuditPublisher
{
    public List<(string Action, IReadOnlyCollection<string> ChangedFields)> Entries { get; } = [];

    /// <summary>Lo que audita el visitante de la tienda pública, sin actor.</summary>
    public List<(string Action, string ResourceType, string ResourceId)> Anonymous { get; } = [];

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) => Entries.Add((action, changedFields));

    public void PublishAnonymous(Guid tenantId, string action, string resourceType, string resourceId, DateTimeOffset at)
    {
        log?.Entries.Add("audit.anonymous");
        Anonymous.Add((action, resourceType, resourceId));
    }
}

internal sealed class FakeCatalog : IStoreCatalogReader
{
    public HashSet<string> CurrenciesWithPrices { get; } = ["COP", "EUR"];

    public Dictionary<Guid, string> Categories { get; } = [];

    public int CurrencyCalls { get; private set; }

    public Task<IReadOnlyDictionary<Guid, string>> CategoryNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            Categories.Where(pair => ids.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

    public Task<IReadOnlyCollection<string>> CurrenciesInUseAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        CurrencyCalls++;
        return Task.FromResult<IReadOnlyCollection<string>>(CurrenciesWithPrices.ToList());
    }

    // Las lecturas públicas no las usa ninguna prueba de ajustes.
    public Task<(IReadOnlyList<StoreProductCard> Items, int Total)> SearchAsync(
        Guid tenantId, string currency, string? q, Guid? categoryId, int page, int pageSize, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<StoreCategoryCount>> CategoriesAsync(
        Guid tenantId, string currency, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<StoreCategoryNode>> CategoryTreeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StoreProductView?> ProductAsync(
        Guid tenantId, Guid productId, string currency, bool includeScales, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>Las variantes que la tienda vende; una que no está acá es una línea no disponible.</summary>
    public Dictionary<Guid, StoreSellableVariant> Sellable { get; } = [];

    public Task<IReadOnlyDictionary<Guid, StoreSellableVariant>> SellableVariantsAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, StoreSellableVariant>>(
            Sellable.Where(pair => variantIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

    public Task<IReadOnlyDictionary<Guid, string>> VariantNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

internal sealed class FakeBillingAccounts : IStoreBillingAccounts
{
    public List<(Guid CompanyId, string Bank, string Number, string Currency)> Validated { get; } = [];

    /// <summary>Los números de cuenta que la «empresa» no tiene: la validación los rechaza.</summary>
    public HashSet<string> UnknownNumbers { get; } = [];

    public List<StoreBillingAccountOption> Options { get; } = [];

    public string? LastListedCurrency { get; private set; }

    public Task<IReadOnlyList<StoreBillingAccountOption>> ListAsync(
        Guid tenantId, string? currency, CancellationToken cancellationToken)
    {
        LastListedCurrency = currency;
        return Task.FromResult<IReadOnlyList<StoreBillingAccountOption>>(Options);
    }

    public Task<StoreBillingAccount> ValidateAsync(
        Guid tenantId, Guid companyId, string bankName, string accountNumber, string currency, CancellationToken cancellationToken)
    {
        Validated.Add((companyId, bankName, accountNumber, currency));
        return UnknownNumbers.Contains(accountNumber)
            ? throw new InvalidOperationException("quotation.billing.account_not_found")
            : Task.FromResult(new StoreBillingAccount(companyId, bankName, accountNumber, currency));
    }
}

internal sealed class FakeFiles : IStoreFileLookup
{
    public Dictionary<Guid, string> PublicImages { get; } = [];

    public Task<string?> FindPublicImageAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult(PublicImages.GetValueOrDefault(fileId));
}

internal sealed class FakeTenants : IStoreTenantLookup
{
    public Task<StoreTenantRef?> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<StoreTenantRef?>(new StoreTenantRef(
            "Origen Botánico",
            "CO",
            true,
            "https://cdn.test/logo.png",
            new StoreRegionalFormat("America/Bogota", "yyyy-MM-dd", "es-CO", "COP", "CO")));
}

/// <summary>El directorio de miembros sembrado: quién está activo, cómo se llama y quién es el owner.</summary>
internal sealed class FakeMembers : IStoreMemberDirectory
{
    public HashSet<Guid> Active { get; } = [];

    /// <summary>La membresía owner del tenant, el asesor de respaldo del checkout.</summary>
    public Guid Owner { get; set; }

    public Dictionary<Guid, string> Names { get; } = [];

    /// <summary>Cada membresía por la que se preguntó si está activa, en orden.</summary>
    public List<Guid> ActiveChecks { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            Names.Where(pair => memberIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

    public Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        ActiveChecks.Add(membershipId);
        return Task.FromResult(Active.Contains(membershipId));
    }

    public Task<Guid> FindOwnerMembershipIdAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(Owner);
}

internal sealed class KnownCountries : IStoreCountries
{
    public bool IsKnown(string country) => country is "CO" or "ES" or "US";
}

/// <summary>Arma el manejador de ajustes con todos sus dobles a la vista de la prueba.</summary>
internal sealed class SettingsRig
{
    public CallLog Log { get; } = new();

    public FakeStores Stores { get; }

    public FakeDomains Domains { get; }

    public FakeUnitOfWork UnitOfWork { get; }

    public FakeAudit Audit { get; } = new();

    public FakeCatalog Catalog { get; } = new();

    public FakeBillingAccounts Accounts { get; } = new();

    public FakeFiles Files { get; } = new();

    public FakeMembers Members { get; } = new();

    public StoreSettingsComposer Composer { get; }

    public IExecutionContext Context { get; }

    public ITenantModules Modules { get; }

    public SettingsRig(
        OnlineStore? seeded = null,
        string[]? permissions = null,
        ITenantModules? modules = null,
        Guid? callerTenant = null)
    {
        Stores = new FakeStores(Log, seeded);
        Domains = new FakeDomains(Log);
        UnitOfWork = new FakeUnitOfWork(Log);
        Context = new FakeExecutionContext(callerTenant ?? StoreTestData.TenantId, permissions ?? [StorePermissions.SettingsManage]);
        Modules = modules ?? FakeTenantModules.AllEnabled();
        Composer = new StoreSettingsComposer(new FakeTenants(), Files, Catalog, Members, new StoreOptions { RootDomain = "lulocrm.com" });
    }

    public UpdateStoreSettingsHandler UpdateHandler() =>
        new(
            Context,
            Modules,
            Stores,
            Domains,
            Catalog,
            Accounts,
            Files,
            Members,
            new KnownCountries(),
            UnitOfWork,
            Audit,
            new FixedClock(Now),
            new StoreOptions { RootDomain = "lulocrm.com" },
            Composer,
            new UpdateStoreSettingsValidator());

    public PublishStoreHandler PublishHandler() =>
        new(Context, Modules, Stores, UnitOfWork, Audit, new FixedClock(Now), Composer, new PublishStoreValidator());

    public GetStoreSettingsHandler GetHandler() =>
        new(Context, Modules, Stores, new FixedClock(Now), Composer);

    public PreviewStoreColorsHandler PreviewHandler() => new(Context, Modules);

    public ListStoreBillingAccountsHandler ListAccountsHandler() => new(Context, Modules, Accounts);

    public static StoreSettingsRequest Body(
        string? slug = "acme",
        string? primaryColor = "#FFD400",
        string? onPrimaryColor = null,
        string? pricingMode = "RetailOnly",
        IReadOnlyList<StoreCurrencyRequest?>? currencies = null,
        string? primaryCurrency = "COP",
        IReadOnlyList<string?>? salesCountries = null,
        IReadOnlyList<Guid>? featured = null,
        Guid? bannerFileId = null,
        string? name = null,
        string? dataPolicyUrl = "https://acme.test/politica",
        string? instagramUrl = null,
        Guid? advisorMembershipId = null,
        bool? showPrices = null) =>
        new(
            slug,
            name,
            primaryColor,
            onPrimaryColor,
            AccentTextColor: null,
            bannerFileId,
            BannerText: null,
            WhatsApp: null,
            Address: null,
            Schedule: null,
            instagramUrl,
            FacebookUrl: null,
            TiktokUrl: null,
            dataPolicyUrl,
            pricingMode,
            currencies ?? [Entry("COP", "A-111")],
            primaryCurrency,
            salesCountries ?? ["co", "es"],
            featured ?? [],
            advisorMembershipId,
            showPrices);

    public static StoreCurrencyRequest Entry(string currency, string account, string? accountCurrency = null) =>
        new(currency, new StoreBillingAccountRequest(CompanyId, "Bancolombia", account, accountCurrency ?? currency));

    public static readonly Guid CompanyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
}
