using BuildingBlocks.Application;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pos.UnitTests;

internal sealed class FakeExecutionContext(Guid tenantId, Guid subjectId, params string[] permissions)
    : IExecutionContext
{
    public Guid SubjectId { get; } = subjectId;

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

internal sealed class FakeMembershipDirectory : IMembershipDirectory
{
    public Dictionary<(Guid UserId, Guid TenantId), Guid> Active { get; } = [];

    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>?>(null);

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Active.TryGetValue((userId, tenantId), out var id) ? id : (Guid?)null);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Active.Where(entry => entry.Key.UserId == userId).Select(entry => entry.Value).ToList());
}

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

internal sealed class FakeTenantClock(DateTimeOffset utcNow) : ITenantClock
{
    public static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(UtcNow, Bogota));
}

public sealed class FakeTenantDefaultCurrency(string currency = "COP") : ITenantDefaultCurrency
{
    public List<Guid> RequestedTenantIds { get; } = [];

    public Task<string> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        RequestedTenantIds.Add(tenantId);
        return Task.FromResult(currency);
    }
}

internal sealed class InMemoryCashSessionRepository : ICashSessionRepository
{
    public List<CashSession> Sessions { get; } = [];

    public Task<CashSession?> FindAsync(Guid tenantId, CashSessionId id, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.SingleOrDefault(session => session.TenantId == tenantId && session.Id == id));

    public Task<CashSession?> FindOpenByCashierAsync(Guid tenantId, MemberId cashier, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.SingleOrDefault(session =>
            session.TenantId == tenantId && session.CashierId == cashier && session.Status == CashSessionStatus.Open));

    public void Add(CashSession session) => Sessions.Add(session);

    public Task<(IReadOnlyList<CashSession> Items, int Total)> ListAsync(
        CashSessionFilter filter, CancellationToken cancellationToken)
    {
        var matches = Sessions
            .Where(session => session.TenantId == filter.TenantId)
            .Where(session => filter.Cashier is not { } cashier || session.CashierId == cashier)
            .Where(session => filter.OpenedFromUtc is not { } from || session.OpenedAt >= from)
            .Where(session => filter.OpenedToUtc is not { } to || session.OpenedAt < to)
            .Where(session => filter.Status is not { } status || session.Status == status)
            .OrderByDescending(session => session.OpenedAt)
            .ToList();
        return Task.FromResult<(IReadOnlyList<CashSession>, int)>(
            (matches.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList(), matches.Count));
    }
}

/// <summary>
/// Sólo encuentra lo "commiteado" (Stored), como una consulta real: lo agregado sin guardar no
/// aparece en FindAsync. FakeUnitOfWork pasa Added a Stored al guardar y lo descarta en Reset.
/// Cada búsqueda queda en <paramref name="calls"/>, para probar en qué orden corre.
/// </summary>
internal sealed class InMemoryPosSaleRepository(InMemoryCashSessionRepository sessions, List<string>? calls = null)
    : IPosSaleRepository
{
    public List<PosSale> Stored { get; } = [];

    public List<PosSale> Added { get; } = [];

    public InMemoryCashSessionRepository Sessions { get; } = sessions;

    public Task<PosSale?> FindAsync(Guid tenantId, PosSaleId id, CancellationToken cancellationToken)
    {
        calls?.Add($"find:{id}");
        return Task.FromResult(Stored.SingleOrDefault(sale => sale.TenantId == tenantId && sale.Id == id));
    }

    public void Add(PosSale sale) => Added.Add(sale);

    public Task<(IReadOnlyList<PosSaleListRow> Items, int Total)> ListAsync(
        PosSaleFilter filter, CancellationToken cancellationToken)
    {
        var matches = Stored
            .Where(sale => sale.TenantId == filter.TenantId)
            .Where(sale => filter.Cashier is not { } cashier || sale.CashierId == cashier)
            .Where(sale => filter.SessionId is not { } sessionId || sale.CashSessionId == sessionId)
            .Where(sale => filter.FromUtc is not { } from || sale.CreatedAt >= from)
            .Where(sale => filter.ToUtc is not { } to || sale.CreatedAt < to)
            .Where(sale => filter.Status is not { } status || sale.Status == status)
            .Where(sale => filter.Number is null || sale.SaleNumber == filter.Number)
            .OrderByDescending(sale => sale.CreatedAt)
            .ToList();
        var rows = matches
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(sale =>
            {
                var session = Sessions.Sessions.Single(item => item.Id == sale.CashSessionId);
                return new PosSaleListRow(sale, session.CashierName, session.Status);
            })
            .ToList();
        return Task.FromResult<(IReadOnlyList<PosSaleListRow>, int)>((rows, matches.Count));
    }

    public void Commit()
    {
        Stored.AddRange(Added);
        Added.Clear();
    }

    public void Discard() => Added.Clear();
}

internal sealed class FakeUnitOfWork(InMemoryPosSaleRepository sales, List<string>? calls = null) : IPosUnitOfWork
{
    public int SaveCalls { get; private set; }

    /// <summary>Hay una transacción abierta: entre BeginTransaction y su Commit, Dispose o un Reset.</summary>
    public bool InTransaction { get; private set; }

    public int ResetCalls { get; private set; }

    public int Commits { get; private set; }

    /// <summary>Cada SaveChanges saca un gancho; si devuelve una excepción, la lanza.</summary>
    public Queue<Func<Exception?>> OnSave { get; } = new();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCalls++;
        if (OnSave.TryDequeue(out var hook) && hook() is { } exception)
        {
            throw exception;
        }

        sales.Commit();
        return Task.FromResult(1);
    }

    public Task<IPosTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (InTransaction)
        {
            // Como EF: una conexión no abre una transacción adentro de otra.
            throw new InvalidOperationException("A transaction is already open.");
        }

        calls?.Add("begin");
        InTransaction = true;
        return Task.FromResult<IPosTransaction>(new FakeTransaction(this));
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        ResetCalls++;
        InTransaction = false;
        sales.Discard();
        return Task.CompletedTask;
    }

    private sealed class FakeTransaction(FakeUnitOfWork owner) : IPosTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            owner.Commits++;
            owner.InTransaction = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.InTransaction = false;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Como pg_advisory_xact_lock: sin transacción abierta no hay candado que dure, así que lo exige.
/// </summary>
internal sealed class FakeSaleIdLock(FakeUnitOfWork unitOfWork, List<string> calls) : IPosSaleIdLock
{
    public Task AcquireAsync(Guid tenantId, PosSaleId saleId, CancellationToken cancellationToken)
    {
        if (!unitOfWork.InTransaction)
        {
            throw new InvalidOperationException("The sale id lock needs an open transaction.");
        }

        calls.Add($"lock:{tenantId}:{saleId}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeNumberGenerator : IPosSaleNumberGenerator
{
    private long _next = 1;

    public int Calls { get; private set; }

    public Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_next++);
    }
}

internal sealed record AuditEntry(
    Guid TenantId, Guid ActorId, string Action, string ResourceType, string ResourceId,
    IReadOnlyCollection<string> ChangedFields);

internal sealed class RecordingAuditPublisher : IPosAuditPublisher
{
    public List<AuditEntry> Entries { get; } = [];

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceType, string resourceId,
        string outcome, IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt) =>
        Entries.Add(new AuditEntry(tenantId, actorId, action, resourceType, resourceId, changedFields));
}

internal sealed class FakeCompanyLookup : IPosCompanyLookup
{
    public List<(Guid TenantId, PosCompanyRef Company)> Companies { get; } = [];

    public Task<IReadOnlyList<PosCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PosCompanyRef>>(Companies
            .Where(entry => entry.TenantId == tenantId && entry.Company.IsActive)
            .Select(entry => entry.Company)
            .ToList());

    public Task<PosCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        Task.FromResult(Companies
            .Where(entry => entry.TenantId == tenantId && entry.Company.Id == companyId)
            .Select(entry => entry.Company)
            .SingleOrDefault());
}

internal sealed class FakeCashierLookup : IPosCashierLookup
{
    public Dictionary<Guid, string> Names { get; } = [];

    public Task<string?> FindNameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        Task.FromResult(Names.TryGetValue(membershipId, out var name) ? name : null);
}

/// <summary>
/// Variantes de la caja. <c>id</c> es el id de la variante; sin <c>productId</c> es la variante por
/// defecto, con el mismo id que su producto (como la migración). Para un producto con opciones se
/// agregan varias con el mismo <c>productId</c> y <c>productName</c>, y con <c>hasOptions</c>.
/// </summary>
internal sealed class FakeProductLookup : IPosProductLookup
{
    public List<(Guid TenantId, PosProductRef Product)> Products { get; } = [];

    private readonly Dictionary<Guid, string> _productNames = [];

    private readonly Dictionary<Guid, bool> _hasOptions = [];

    /// <param name="price">The COP price; <paramref name="priceUsd"/> the USD one. <paramref name="prices"/>,
    /// when given, replaces both: the way to put an EUR price on a product.</param>
    /// <param name="name">El nombre visible de la variante.</param>
    /// <param name="productName">El nombre del producto, sin opciones; por defecto, <paramref name="name"/>.</param>
    /// <param name="hasOptions">Si el producto tiene opciones, como <c>OptionIds.Count &gt; 0</c> en el
    /// adaptador real: lo dice el producto, no el número de variantes (un producto con opciones puede
    /// tener una sola variante activa). Basta con pasarlo en una de sus variantes.</param>
    public PosProductRef Add(
        string code, string name, decimal? price, int tax, bool active = true, Guid? id = null, Guid? tenantId = null,
        decimal? priceUsd = null, IReadOnlyDictionary<string, decimal>? prices = null,
        Guid? productId = null, string? productName = null, string? barcode = null, bool hasOptions = false)
    {
        var variantId = id ?? Guid.CreateVersion7();
        var owner = productId ?? variantId;
        var product = new PosProductRef(
            variantId, owner, code, barcode, name, active, prices ?? PricesOf(price, priceUsd), tax, null);
        _productNames.TryAdd(owner, productName ?? name);
        _hasOptions[owner] = _hasOptions.GetValueOrDefault(owner) || hasOptions;
        Products.Add((tenantId ?? PosFixtures.TenantId, product));
        return product;
    }

    private static Dictionary<string, decimal> PricesOf(decimal? cop, decimal? usd)
    {
        var prices = new Dictionary<string, decimal>();
        if (cop is { } copPrice) prices["COP"] = copPrice;
        if (usd is { } usdPrice) prices["USD"] = usdPrice;
        return prices;
    }

    public void Replace(PosProductRef product)
    {
        var index = Products.FindIndex(entry => entry.Product.VariantId == product.VariantId);
        Products[index] = (Products[index].TenantId, product);
    }

    // Como el adaptador: productos con alguna variante activa que coincide por nombre del producto,
    // Code o código de barras, cada uno con sus variantes activas.
    public Task<(IReadOnlyList<PosCatalogProductRef> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matches = Products
            .Where(entry => entry.TenantId == tenantId && entry.Product.IsActive)
            .Select(entry => entry.Product)
            .GroupBy(variant => variant.ProductId)
            .Where(group => string.IsNullOrWhiteSpace(search)
                || _productNames[group.Key].Contains(search, StringComparison.OrdinalIgnoreCase)
                || group.Any(variant => variant.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (variant.Barcode?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Select(group => new PosCatalogProductRef(
                group.Key, _productNames[group.Key], _hasOptions[group.Key], group.First().ImageUrl, group.ToArray()))
            .ToList();
        return Task.FromResult<(IReadOnlyList<PosCatalogProductRef>, int)>(
            (matches.Skip((page - 1) * pageSize).Take(pageSize).ToList(), matches.Count));
    }

    // Como el adaptador: primero el código de barras, después el Code.
    public Task<PosProductRef?> FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken)
    {
        var own = Products.Where(entry => entry.TenantId == tenantId).Select(entry => entry.Product).ToList();
        return Task.FromResult(
            own.SingleOrDefault(product => string.Equals(product.Barcode, code, StringComparison.Ordinal))
            ?? own.SingleOrDefault(product => string.Equals(product.Code, code, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyDictionary<Guid, PosProductRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PosProductRef>>(Products
            .Where(entry => entry.TenantId == tenantId && variantIds.Contains(entry.Product.VariantId))
            .ToDictionary(entry => entry.Product.VariantId, entry => entry.Product));
}

/// <summary>Todo lo que un handler del POS necesita, con el cajero del ejemplo ya miembro activo.</summary>
internal sealed class PosTestBed
{
    public static readonly Guid UserId = Guid.Parse("01930000-0000-7000-8000-000000000001");

    public PosTestBed()
    {
        Sales = new InMemoryPosSaleRepository(Sessions, Calls);
        UnitOfWork = new FakeUnitOfWork(Sales, Calls);
        SaleIdLock = new FakeSaleIdLock(UnitOfWork, Calls);
        Memberships.Active[(UserId, PosFixtures.TenantId)] = PosFixtures.Cashier.Value;
        Cashiers.Names[PosFixtures.Cashier.Value] = "Laura Gómez";
    }

    public FakeMembershipDirectory Memberships { get; } = new();

    public InMemoryCashSessionRepository Sessions { get; } = new();

    public InMemoryPosSaleRepository Sales { get; }

    public FakeUnitOfWork UnitOfWork { get; }

    /// <summary>Lo que hicieron la unidad de trabajo, el candado y el repositorio de ventas, en orden.</summary>
    public List<string> Calls { get; } = [];

    public FakeSaleIdLock SaleIdLock { get; }

    public FakeNumberGenerator Numbers { get; } = new();

    public RecordingAuditPublisher Audit { get; } = new();

    public FakeCompanyLookup Companies { get; } = new();

    public FakeCashierLookup Cashiers { get; } = new();

    public FakeProductLookup Products { get; } = new();

    public FakeClock Clock { get; } = new(PosFixtures.Now);

    public FakeTenantClock TenantClock { get; } = new(PosFixtures.Now);

    /// <summary>La moneda por defecto del tenant que lee la apertura de caja.</summary>
    public FakeTenantDefaultCurrency DefaultCurrency { get; init; } = new();

    /// <summary>El usuario autenticado de las pruebas; instancia para que Context no sea estático (CA1822).</summary>
    public Guid SubjectId { get; } = UserId;

    public FakeExecutionContext Context(params string[] permissions) =>
        new(PosFixtures.TenantId, SubjectId, permissions);

    public CashSession OpenSessionInStore(decimal openingFloat = 100_000m, string currency = "COP")
    {
        var session = PosFixtures.OpenSession(openingFloat, currency: currency);
        Sessions.Add(session);
        return session;
    }

    /// <summary>Los tres productos del ejemplo trabajado, con sus ids de PosFixtures.</summary>
    public void AddWorkedExampleProducts()
    {
        Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19, id: PosFixtures.Shampoo);
        Products.Add("AV-01", "Avena granel (kg)", 5_000m, 0, id: PosFixtures.Avena);
        Products.Add("JB-03", "Jabón", 2_990m, 5, id: PosFixtures.Jabon);
    }

    public PosCompanyRef AddCompany(string name = "Origen Botánico SAS", bool active = true, Guid? tenantId = null)
    {
        var company = new PosCompanyRef(Guid.CreateVersion7(), name, "900123456-1", "Cra 50 # 10-20", "6045551234", active);
        Companies.Companies.Add((tenantId ?? PosFixtures.TenantId, company));
        return company;
    }
}
