using BuildingBlocks.Application;
using Modules.Reporting.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>Un contexto de ejecucion con el tenant y los permisos que la prueba elija. Es lo
/// unico que <c>ReportingAuthorization</c> mira.</summary>
internal sealed class FakeExecutionContext(Guid tenantId, params string[] permissions)
    : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.Parse("01900000-0000-7000-8000-0000000000ff");

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission);
}

/// <summary>
/// El directorio de membresías con la respuesta que la prueba elija. Por defecto quien llama
/// tiene membresía activa con id <see cref="CallerMembershipId"/>, que es el asesor al que un
/// llamador sin <c>reporting.all_advisors.read</c> queda acotado. Nulo simula a alguien sin
/// membresía activa en el tenant.
/// </summary>
internal sealed class FakeMembershipDirectory(Guid? activeMembershipId) : IMembershipDirectory
{
    public static readonly Guid CallerMembershipId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    public FakeMembershipDirectory()
        : this(CallerMembershipId)
    {
    }

    public List<(Guid UserId, Guid TenantId)> Lookups { get; } = [];

    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Reporting no pregunta por roles.");

    public Task<Guid?> FindActiveMembershipIdAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        Lookups.Add((userId, tenantId));
        return Task.FromResult(activeMembershipId);
    }

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Reporting no pregunta por membresías históricas.");
}

/// <summary>El calendario de un tenant en un huso fijo, Bogotá por defecto (spec 2026-09-17). Sin
/// instante, media tarde del 3 de septiembre de 2026 en UTC: las pruebas que no miran el hoy no
/// tienen que inventarlo.</summary>
internal sealed class FixedTenantClock(DateTimeOffset utcNow, string timeZoneId = "America/Bogota")
    : ITenantClock
{
    public FixedTenantClock()
        : this(new DateTimeOffset(2026, 9, 3, 14, 30, 0, TimeSpan.Zero))
    {
    }

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
}

/// <summary>
/// Un origen de pedidos que devuelve lo que se le cargue y **recuerda con que argumentos lo
/// llamaron**: la mitad de lo que se prueba de un handler de listado es justamente que la pagina
/// que le llega al origen sea la normalizada, no la cruda.
/// </summary>
internal sealed class FakeOrdersReportSource : IOrdersReportSource
{
    public IReadOnlyList<OrdersReportItemDto> Items { get; set; } = [];

    public int Total { get; set; }

    public OrdersReportCriteria? LastCriteria { get; private set; }

    public int? LastPage { get; private set; }

    public int? LastPageSize { get; private set; }

    /// <summary>Lo que devuelve el primer <c>SummarizeAsync</c>: el periodo pedido.</summary>
    public OrdersReportAggregate Aggregate { get; set; } = new(0, 0m, 0m, 0m, [], [], []);

    /// <summary>
    /// Lo que devuelve el segundo: la ventana anterior. Nulo significa que la prueba no espera
    /// una segunda consulta; si igual llega se devuelve <see cref="Aggregate"/>, porque quien
    /// delata la consulta de mas es el conteo de <see cref="SummarizedCriteria"/> y no un nulo
    /// explotando a mitad del handler.
    /// </summary>
    public OrdersReportAggregate? PrecedingAggregate { get; set; }

    /// <summary>Los criterios de cada <c>SummarizeAsync</c>, en orden: el resumen consulta una o
    /// dos veces segun haya periodo anterior, y cual es cual importa.</summary>
    public List<OrdersReportCriteria> SummarizedCriteria { get; } = [];

    public int? LastRankSize { get; private set; }

    public Task<OrdersReportAggregate> SummarizeAsync(
        OrdersReportCriteria criteria,
        int rankSize,
        CancellationToken cancellationToken)
    {
        var isPreceding = SummarizedCriteria.Count > 0;
        SummarizedCriteria.Add(criteria);
        LastRankSize = rankSize;
        return Task.FromResult(isPreceding ? PrecedingAggregate ?? Aggregate : Aggregate);
    }

    public Task<(IReadOnlyList<OrdersReportItemDto> Items, int Total)> ListAsync(
        OrdersReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        LastCriteria = criteria;
        LastPage = page;
        LastPageSize = pageSize;
        return Task.FromResult((Items, Total));
    }
}

/// <summary>
/// Un origen de cotizaciones que recuerda con que argumentos lo llamaron. El resumen consulta una
/// o dos veces segun haya periodo anterior, y **cuales opciones llevo cada una** es la mitad de lo
/// que hay que probar: la segunda no pide ranking ni cola de vencimientos.
/// </summary>
internal sealed class FakeQuotationsReportSource : IQuotationsReportSource
{
    public QuotationsReportAggregate Aggregate { get; set; } = EmptyAggregate();

    public QuotationsReportAggregate? PrecedingAggregate { get; set; }

    public List<QuotationsReportCriteria> SummarizedCriteria { get; } = [];

    public List<QuotationsSummaryOptions> SummarizedOptions { get; } = [];

    public QuotationsReportCriteria? LastCriteria { get; private set; }

    public Task<QuotationsReportAggregate> SummarizeAsync(
        QuotationsReportCriteria criteria,
        QuotationsSummaryOptions options,
        CancellationToken cancellationToken)
    {
        var isPreceding = SummarizedCriteria.Count > 0;
        SummarizedCriteria.Add(criteria);
        SummarizedOptions.Add(options);
        return Task.FromResult(isPreceding ? PrecedingAggregate ?? Aggregate : Aggregate);
    }

    public Task<(IReadOnlyList<QuotationsReportItemDto> Items, int Total)> ListAsync(
        QuotationsReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        LastCriteria = criteria;
        return Task.FromResult(((IReadOnlyList<QuotationsReportItemDto>)[], 0));
    }

    public static QuotationsReportAggregate EmptyAggregate(
        int quotationCount = 0,
        decimal total = 0m) =>
        new(
            quotationCount,
            total,
            0m,
            total,
            [],
            [],
            [],
            new QuotationValidityDto(
                new ReportBucketDto(0, 0m),
                new ReportBucketDto(0, 0m),
                new ReportBucketDto(0, 0m),
                new ReportBucketDto(0, 0m),
                0),
            []);
}

/// <summary>
/// Un origen de cambios de precio que recuerda con qué criterio y con qué tope lo llamaron. Igual
/// que el de cotizaciones, el resumen lo consulta una o dos veces según haya periodo anterior, y
/// **con qué tope lo llamó cada vez** es la mitad de lo que hay que probar: de la ventana anterior
/// sólo se lee el conteo, así que no lleva ranking.
/// </summary>
internal sealed class FakePriceChangeReportSource : IPriceChangeReportSource
{
    public PriceChangeReportAggregate Aggregate { get; set; } = EmptyAggregate();

    public PriceChangeReportAggregate? PrecedingAggregate { get; set; }

    public List<PriceChangeReportCriteria> SummarizedCriteria { get; } = [];

    public List<int> SummarizedRankSizes { get; } = [];

    public Task<PriceChangeReportAggregate> SummarizeAsync(
        PriceChangeReportCriteria criteria,
        int rankSize,
        CancellationToken cancellationToken)
    {
        var isPreceding = SummarizedCriteria.Count > 0;
        SummarizedCriteria.Add(criteria);
        SummarizedRankSizes.Add(rankSize);
        return Task.FromResult(isPreceding ? PrecedingAggregate ?? Aggregate : Aggregate);
    }

    public Task<(IReadOnlyList<PriceChangeReportRow> Rows, int Total)> ListAsync(
        PriceChangeReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromResult(((IReadOnlyList<PriceChangeReportRow>)[], 0));

    public static PriceChangeReportAggregate EmptyAggregate(
        int changeCount = 0,
        int increaseCount = 0,
        int decreaseCount = 0) =>
        new(changeCount, 0, increaseCount, decreaseCount, [], [], []);
}

/// <summary>
/// Un origen de clientes que recuerda con qué criterio y con qué tope lo llamaron. Igual que sus
/// tres hermanos, el resumen lo consulta una o dos veces según haya periodo anterior, y **con qué
/// tope lo llamó cada vez** es la mitad de lo que hay que probar: de la ventana anterior sólo se
/// lee el conteo, así que no lleva ranking.
/// </summary>
internal sealed class FakeCustomerReportSource : ICustomerReportSource
{
    public CustomerReportAggregate Aggregate { get; set; } = EmptyAggregate();

    public CustomerReportAggregate? PrecedingAggregate { get; set; }

    public List<CustomerReportCriteria> SummarizedCriteria { get; } = [];

    public List<int> SummarizedRankSizes { get; } = [];

    public Task<CustomerReportAggregate> SummarizeAsync(
        CustomerReportCriteria criteria,
        int rankSize,
        CancellationToken cancellationToken)
    {
        var isPreceding = SummarizedCriteria.Count > 0;
        SummarizedCriteria.Add(criteria);
        SummarizedRankSizes.Add(rankSize);
        return Task.FromResult(isPreceding ? PrecedingAggregate ?? Aggregate : Aggregate);
    }

    public Task<(IReadOnlyList<CustomerReportItemDto> Items, int Total)> ListAsync(
        CustomerReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromResult(((IReadOnlyList<CustomerReportItemDto>)[], 0));

    public static CustomerReportAggregate EmptyAggregate(
        int customerCount = 0,
        int activeCount = 0) =>
        new(customerCount, activeCount, [], [], []);
}
