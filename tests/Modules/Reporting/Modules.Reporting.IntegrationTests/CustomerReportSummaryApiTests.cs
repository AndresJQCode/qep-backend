using System.Net;
using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// El resumen del reporte de clientes: los agregados que el panel dibuja sin bajarse una sola fila.
///
/// Lo que se prueba aca y no en el handler es **la consulta**, y este resumen tiene dos que una
/// prueba unitaria contra un doble no toca ni de lejos:
///
/// - La serie mensual agrupa por <c>CreatedAt</c> en UTC, con la misma expresion que EF tiene que
///   saber traducir a un <c>date_part</c>.
/// - El reparto por departamento agrupa por **la ciudad de la direccion principal**, que en LINQ es
///   una subconsulta correlacionada dentro de un <c>GROUP BY</c>. Si EF no la traduce, la evalua en
///   cliente o revienta — y las dos cosas solo se ven contra PostgreSQL real.
/// </summary>
public sealed class CustomerReportSummaryApiTests
{
    [Fact]
    public async Task SummaryCountsTheCustomersAndHowManyAreActive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var kept = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var dropped = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var deactivated = await client.PostAsync(
            $"/api/v1/tenants/{tenant.TenantId}/customers/{dropped.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        deactivated.EnsureSuccessStatusCode();

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<CustomerReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);

        Assert.Equal(2, summary.CustomerCount);
        Assert.Equal(1, summary.ActiveCount);
        // Los inactivos son la resta y no un campo del contrato.
        Assert.Equal(1, summary.CustomerCount - summary.ActiveCount);

        // Los dos se dieron de alta hoy, asi que la serie tiene un solo mes con los dos.
        var month = Assert.Single(summary.Monthly);
        Assert.Equal(2, month.Count);
        Assert.Equal(DateTime.UtcNow.Year, month.Year);
        Assert.Equal(DateTime.UtcNow.Month, month.Month);

        // Cada cliente sembrado trae su propia clasificacion, asi que son dos grupos de uno.
        Assert.Equal(2, summary.ByClassification.Count);
        Assert.All(summary.ByClassification, entry => Assert.Equal(1, entry.Count));
        Assert.Contains(summary.ByClassification, entry => entry.Id == kept.ClassificationId);
        Assert.All(
            summary.ByClassification,
            entry => Assert.False(string.IsNullOrWhiteSpace(entry.Label)));

        // Sin rango de fechas no hay ventana anterior contra la cual comparar.
        Assert.Null(summary.Previous);
    }

    /// <summary>
    /// El reparto por departamento es el que no se puede agrupar entero en la base: el cliente
    /// guarda ciudad, y el departamento vive del otro lado de la frontera de <c>Geography</c>. Esta
    /// prueba existe para que esa consulta se ejecute de verdad contra PostgreSQL.
    /// </summary>
    [Fact]
    public async Task SummaryGroupsByTheDepartmentOfThePrincipalAddress()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        await CreateActiveCustomerAsync(client, tenant.TenantId);
        await CreateActiveCustomerAsync(client, tenant.TenantId);

        var summary = await client.GetFromJsonAsync<CustomerReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        // El harness siembra los dos en la misma ciudad, asi que caen en el mismo departamento.
        var department = Assert.Single(summary.ByDepartment);
        Assert.NotNull(department.Id);
        Assert.False(string.IsNullOrWhiteSpace(department.Label));
        Assert.Equal(1, department.EntityCount);
        Assert.Equal(2, department.Count);
    }

    [Fact]
    public async Task SummaryOfATenantWithoutCustomersIsAllZeros()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var summary = await client.GetFromJsonAsync<CustomerReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.CustomerCount);
        Assert.Equal(0, summary.ActiveCount);
        Assert.Empty(summary.Monthly);
        Assert.Empty(summary.ByClassification);
        Assert.Empty(summary.ByDepartment);
        Assert.Null(summary.Previous);
    }

    /// <summary>El resumen toma exactamente los mismos filtros que el listado: si contara otro
    /// conjunto, el panel y el Excel de la misma pantalla dirian cosas distintas.</summary>
    [Fact]
    public async Task SummaryFiltersByActiveStateLikeTheList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var deactivated = await client.PostAsync(
            $"/api/v1/tenants/{tenant.TenantId}/customers/{customer.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        deactivated.EnsureSuccessStatusCode();

        var active = await client.GetFromJsonAsync<CustomerReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary?isActive=true",
            TestContext.Current.CancellationToken);
        var inactive = await client.GetFromJsonAsync<CustomerReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary?isActive=false",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, active?.CustomerCount);
        Assert.Equal(1, inactive?.CustomerCount);
    }

    /// <summary>
    /// El rango corta por fecha de alta, que es la unica fecha que tiene un cliente. Un rango
    /// enteramente en el pasado no puede alcanzar a un cliente creado recien, y trae ademas la
    /// ventana anterior — que existe solo cuando hay dos puntas.
    /// </summary>
    [Fact]
    public async Task SummaryFiltersByTheCreationDateAndComparesAgainstThePrecedingWindow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        await CreateActiveCustomerAsync(client, tenant.TenantId);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var current = await client.GetFromJsonAsync<CustomerReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary"
                + $"?from={today.AddDays(-30):yyyy-MM-dd}&to={today:yyyy-MM-dd}",
            TestContext.Current.CancellationToken);
        var longAgo = await client.GetFromJsonAsync<CustomerReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary"
                + "?from=2020-01-01&to=2020-12-31",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, current?.CustomerCount);
        Assert.Equal(0, longAgo?.CustomerCount);

        // Con las dos puntas hay ventana anterior, y en ella no habia ningun cliente.
        Assert.NotNull(current?.Previous);
        Assert.Equal(0, current.Previous.CustomerCount);
    }

    [Fact]
    public async Task SummaryRejectsARangeThatEndsBeforeItStarts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary?from=2026-03-31&to=2026-03-01",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SummaryRejectsAnotherTenantsReport()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(Guid.CreateVersion7())}/customers/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SummaryRejectsACallerWithoutTheReportingPermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, SeedOnlyPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
