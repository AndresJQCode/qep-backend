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
