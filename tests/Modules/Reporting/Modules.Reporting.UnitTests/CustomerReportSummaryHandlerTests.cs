using BuildingBlocks.Application;
using FluentValidation;
using Modules.Reporting.Application;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El handler del resumen de clientes.
///
/// Comparte con sus tres hermanos el orden no negociable —autorizar, validar, recién después tocar
/// el origen— y la regla de la ventana anterior. Lo suyo es **qué se puede medir de un cliente**:
/// un cliente no tiene monto, así que acá no hay ni un peso sumado. Lo que sí significa algo es
/// cuántos son, cuántos están activos, cuándo entraron y cómo se reparten por clasificación y por
/// departamento — y eso vale lo mismo en cualquier moneda.
///
/// El rango de fechas corta por **fecha de alta**: es la única fecha que tiene un cliente. Por eso
/// "el periodo anterior" acá compara altas contra altas, y no cartera contra cartera.
/// </summary>
public sealed class CustomerReportSummaryHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid Classification = Guid.Parse("01900000-0000-7000-8000-0000000000d1");
    private static readonly Guid Department = Guid.Parse("01900000-0000-7000-8000-0000000000e1");

    [Fact]
    public async Task SummarizingRejectsATenantThatIsNotTheCallersOne()
    {
        var source = new FakeCustomerReportSource();
        var handler = Handler(source, OtherTenant, ReportingPermissions.CustomerRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new GetCustomerReportSummaryQuery(Filter()),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(source.SummarizedCriteria);
    }

    /// <summary>El reporte de clientes es sólo del Administrador: el permiso de ventas —que sí
    /// tiene un asesor— no alcanza para verlo.</summary>
    [Fact]
    public async Task SummarizingRejectsACallerWithOnlyTheSalesPermission()
    {
        var source = new FakeCustomerReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.SalesRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new GetCustomerReportSummaryQuery(Filter()),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(source.SummarizedCriteria);
    }

    /// <summary>
    /// El rango dado vuelta se rechaza antes de tocar la base, igual que en los otros tres.
    ///
    /// Es la primera regla que gana <c>CustomerReportFilterValidator</c>, que hasta ahora estaba
    /// declarado vacío porque los tres filtros eran tipados y no había nada que validar.
    /// </summary>
    [Fact]
    public async Task SummarizingRejectsARangeThatEndsBeforeItStarts()
    {
        var source = new FakeCustomerReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.CustomerRead);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                new GetCustomerReportSummaryQuery(
                    Filter(from: new DateOnly(2026, 3, 31), to: new DateOnly(2026, 3, 1))),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "To");
        Assert.Empty(source.SummarizedCriteria);
    }

    /// <summary>
    /// Sin rango no hay ventana anterior contra la cual comparar, así que el origen se consulta una
    /// sola vez. Es el caso por defecto de esta pantalla: sin filtro, el panel describe la cartera
    /// entera y no un periodo.
    /// </summary>
    [Fact]
    public async Task SummarizingWithoutADateRangeAsksTheSourceOnceAndHasNoComparison()
    {
        var source = new FakeCustomerReportSource
        {
            Aggregate = FakeCustomerReportSource.EmptyAggregate(
                customerCount: 1284, activeCount: 1150),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.CustomerRead);

        var summary = await handler.HandleAsync(
            new GetCustomerReportSummaryQuery(Filter()),
            TestContext.Current.CancellationToken);

        Assert.Single(source.SummarizedCriteria);
        Assert.Null(summary.Previous);
        Assert.Equal(1284, summary.CustomerCount);
        Assert.Equal(1150, summary.ActiveCount);
    }

    /// <summary>
    /// Los inactivos **no viajan en el contrato**: son la resta, y un campo más es un campo más que
    /// puede desincronizarse de los dos que lo definen. Mismo criterio que el ticket promedio de
    /// ventas, que el panel deriva de <c>total / saleCount</c>.
    ///
    /// Acá se puede porque el reparto es binario y exhaustivo, a diferencia de la dirección de un
    /// cambio de precio: ahí existe un tercer grupo —los que no se movieron— y por eso ese resumen
    /// sí manda las dos puntas.
    /// </summary>
    [Fact]
    public async Task TheSummaryDoesNotCarryTheInactiveCountBecauseItIsTheSubtraction()
    {
        var source = new FakeCustomerReportSource
        {
            Aggregate = FakeCustomerReportSource.EmptyAggregate(
                customerCount: 1284, activeCount: 1150),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.CustomerRead);

        var summary = await handler.HandleAsync(
            new GetCustomerReportSummaryQuery(Filter()),
            TestContext.Current.CancellationToken);

        Assert.Equal(134, summary.CustomerCount - summary.ActiveCount);
    }

    [Fact]
    public async Task SummarizingAsksForTheRankOfGroups()
    {
        var source = new FakeCustomerReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.CustomerRead);

        await handler.HandleAsync(
            new GetCustomerReportSummaryQuery(Filter()),
            TestContext.Current.CancellationToken);

        Assert.Equal(ReportSummaryRules.RankSize, Assert.Single(source.SummarizedRankSizes));
    }

    [Fact]
    public async Task SummarizingWithADateRangeComparesAgainstTheWindowImmediatelyBefore()
    {
        var source = new FakeCustomerReportSource
        {
            Aggregate = FakeCustomerReportSource.EmptyAggregate(customerCount: 48),
            PrecedingAggregate = FakeCustomerReportSource.EmptyAggregate(customerCount: 31),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.CustomerRead);

        var summary = await handler.HandleAsync(
            new GetCustomerReportSummaryQuery(
                Filter(from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 1, 31))),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, source.SummarizedCriteria.Count);
        var preceding = source.SummarizedCriteria[1];
        Assert.Equal(new DateOnly(2025, 12, 1), preceding.From);
        Assert.Equal(new DateOnly(2025, 12, 31), preceding.To);

        Assert.NotNull(summary.Previous);
        Assert.Equal(31, summary.Previous.CustomerCount);
    }

    [Fact]
    public async Task TheComparisonKeepsEveryOtherFilter()
    {
        var source = new FakeCustomerReportSource
        {
            PrecedingAggregate = FakeCustomerReportSource.EmptyAggregate(),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.CustomerRead);

        await handler.HandleAsync(
            new GetCustomerReportSummaryQuery(Filter(
                from: new DateOnly(2026, 1, 1),
                to: new DateOnly(2026, 1, 31),
                isActive: true,
                classificationId: Classification,
                departmentId: Department)),
            TestContext.Current.CancellationToken);

        var preceding = source.SummarizedCriteria[1];
        Assert.Equal(Tenant, preceding.TenantId);
        Assert.True(preceding.IsActive);
        Assert.Equal(Classification, preceding.ClassificationId);
        Assert.Equal(Department, preceding.DepartmentId);
    }

    /// <summary>De la ventana anterior sólo se lee el conteo, así que no se le piden los rankings:
    /// son consultas que nadie mira, y "los departamentos del periodo anterior" no aparece en
    /// ninguna pantalla.</summary>
    [Fact]
    public async Task TheComparisonDoesNotAskForTheRanking()
    {
        var source = new FakeCustomerReportSource
        {
            PrecedingAggregate = FakeCustomerReportSource.EmptyAggregate(),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.CustomerRead);

        await handler.HandleAsync(
            new GetCustomerReportSummaryQuery(
                Filter(from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 1, 31))),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, source.SummarizedRankSizes[1]);
    }

    private static GetCustomerReportSummaryHandler Handler(
        ICustomerReportSource source,
        Guid callerTenant,
        params string[] permissions) =>
        new(
            source,
            new CustomerReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions));

    private static CustomerReportFilter Filter(
        DateOnly? from = null,
        DateOnly? to = null,
        bool? isActive = null,
        Guid? classificationId = null,
        Guid? departmentId = null) =>
        new(Tenant, from, to, isActive, classificationId, departmentId);
}
