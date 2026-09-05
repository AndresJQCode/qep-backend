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

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>
/// Un origen de ventas que devuelve lo que se le cargue y **recuerda con que argumentos lo
/// llamaron**: la mitad de lo que se prueba de un handler de listado es justamente que la pagina
/// que le llega al origen sea la normalizada, no la cruda.
/// </summary>
internal sealed class FakeSalesReportSource : ISalesReportSource
{
    public IReadOnlyList<SalesReportItemDto> Items { get; set; } = [];

    public int Total { get; set; }

    public SalesReportCriteria? LastCriteria { get; private set; }

    public int? LastPage { get; private set; }

    public int? LastPageSize { get; private set; }

    public int? LastExportLimit { get; private set; }

    /// <summary>Lo que devuelve el primer <c>SummarizeAsync</c>: el periodo pedido.</summary>
    public SalesReportAggregate Aggregate { get; set; } = new(0, 0m, 0m, 0m, [], [], []);

    /// <summary>
    /// Lo que devuelve el segundo: la ventana anterior. Nulo significa que la prueba no espera
    /// una segunda consulta; si igual llega se devuelve <see cref="Aggregate"/>, porque quien
    /// delata la consulta de mas es el conteo de <see cref="SummarizedCriteria"/> y no un nulo
    /// explotando a mitad del handler.
    /// </summary>
    public SalesReportAggregate? PrecedingAggregate { get; set; }

    /// <summary>Los criterios de cada <c>SummarizeAsync</c>, en orden: el resumen consulta una o
    /// dos veces segun haya periodo anterior, y cual es cual importa.</summary>
    public List<SalesReportCriteria> SummarizedCriteria { get; } = [];

    public int? LastRankSize { get; private set; }

    public Task<SalesReportAggregate> SummarizeAsync(
        SalesReportCriteria criteria,
        int rankSize,
        CancellationToken cancellationToken)
    {
        var isPreceding = SummarizedCriteria.Count > 0;
        SummarizedCriteria.Add(criteria);
        LastRankSize = rankSize;
        return Task.FromResult(isPreceding ? PrecedingAggregate ?? Aggregate : Aggregate);
    }

    public Task<(IReadOnlyList<SalesReportItemDto> Items, int Total)> ListAsync(
        SalesReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        LastCriteria = criteria;
        LastPage = page;
        LastPageSize = pageSize;
        return Task.FromResult((Items, Total));
    }

    public Task<IReadOnlyList<SalesReportItemDto>> ListForExportAsync(
        SalesReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken)
    {
        LastCriteria = criteria;
        LastExportLimit = limit;
        return Task.FromResult(Items);
    }
}

/// <summary>Un armador de Excel que no arma nada: devuelve bytes de juguete y anota cuantas filas
/// recibio. Que el <c>.xlsx</c> este bien formado lo verifican las pruebas de integracion, que
/// levantan el builder real.</summary>
internal sealed class FakeReportExcelBuilder : IReportExcelBuilder
{
    public int? SalesRowCount { get; private set; }

    public int? QuotationsRowCount { get; private set; }

    public int? PriceChangesRowCount { get; private set; }

    public int? CustomersRowCount { get; private set; }

    public DateTimeOffset? GeneratedAt { get; private set; }

    public ReportFile BuildSales(
        IReadOnlyList<SalesReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        SalesRowCount = rows.Count;
        GeneratedAt = generatedAt;
        return new ReportFile([1, 2, 3], "reporte-ventas.xlsx");
    }

    public ReportFile BuildQuotations(
        IReadOnlyList<QuotationsReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        QuotationsRowCount = rows.Count;
        GeneratedAt = generatedAt;
        return new ReportFile([1, 2, 3], "reporte-cotizaciones.xlsx");
    }

    public ReportFile BuildPriceChanges(
        IReadOnlyList<PriceChangeReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        PriceChangesRowCount = rows.Count;
        GeneratedAt = generatedAt;
        return new ReportFile([1, 2, 3], "reporte-cambios-precio.xlsx");
    }

    public ReportFile BuildCustomers(
        IReadOnlyList<CustomerReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        CustomersRowCount = rows.Count;
        GeneratedAt = generatedAt;
        return new ReportFile([1, 2, 3], "reporte-clientes.xlsx");
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
        CancellationToken cancellationToken) =>
        Task.FromResult(((IReadOnlyList<QuotationsReportItemDto>)[], 0));

    public Task<IReadOnlyList<QuotationsReportItemDto>> ListForExportAsync(
        QuotationsReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<QuotationsReportItemDto>)[]);

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

    public Task<IReadOnlyList<PriceChangeReportRow>> ListForExportAsync(
        PriceChangeReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<PriceChangeReportRow>)[]);

    public static PriceChangeReportAggregate EmptyAggregate(
        int changeCount = 0,
        int increaseCount = 0,
        int decreaseCount = 0) =>
        new(changeCount, 0, increaseCount, decreaseCount, [], [], []);
}
