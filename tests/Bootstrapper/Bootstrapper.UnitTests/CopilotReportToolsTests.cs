using System.Text.Json;
using Bootstrapper.Copilot;
using BuildingBlocks.Application;
using Modules.Ai.Application;
using Modules.Reporting.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Plan fase 2, C2.5 (spec 2026-11-17 copiloto §6 y D-C5): las herramientas de reportes despachan la misma consulta que su endpoint,
/// con el tenant del hilo, y las listas no pasan de 20 filas.</summary>
public sealed class CopilotReportToolsTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly CopilotPrincipal Principal = new(Tenant, Guid.Parse("01900000-0000-7000-8000-0000000000b1"));
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, Type> Endpoints() => new()
    {
        { "get_orders_summary", typeof(GetOrdersReportSummaryQuery) },
        { "list_orders_report", typeof(ListOrdersReportQuery) },
        { "get_quotations_summary", typeof(GetQuotationsReportSummaryQuery) },
        { "list_quotations_report", typeof(ListQuotationsReportQuery) },
        { "get_customers_summary", typeof(GetCustomerReportSummaryQuery) },
        { "get_margin_summary", typeof(GetMarginReportSummaryQuery) },
        { "get_price_changes_summary", typeof(GetPriceChangeReportSummaryQuery) },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EachReportToolDispatchesTheSameQueryAsItsEndpoint(string name, Type query)
    {
        var dispatcher = new CannedDispatcher();
        var tool = Tool(name);

        var result = await tool.ExecuteAsync(Call(EmptyArguments(tool), dispatcher), Ct);

        Assert.False(result.IsError);
        Assert.Equal(query, Assert.Single(dispatcher.Queries).GetType());
        Assert.Equal(TenantModuleKeys.Reporting, tool.Contract.Module);
        Assert.StartsWith("reporting.", tool.Contract.Permission, StringComparison.Ordinal);
    }

    // Spec §6: get_orders_summary con reporting.orders.read; las de margen y precios, con su permiso propio.
    [Fact]
    public void EachReportToolAsksForThePermissionOfItsEndpoint()
    {
        var permissions = CopilotReportTools.All().ToDictionary(tool => tool.Contract.Name, tool => tool.Contract.Permission);

        Assert.Equal(ReportingPermissions.OrdersRead, permissions["get_orders_summary"]);
        Assert.Equal(ReportingPermissions.OrdersRead, permissions["list_orders_report"]);
        Assert.Equal(ReportingPermissions.QuotationRead, permissions["get_quotations_summary"]);
        Assert.Equal(ReportingPermissions.QuotationRead, permissions["list_quotations_report"]);
        Assert.Equal(ReportingPermissions.CustomerRead, permissions["get_customers_summary"]);
        Assert.Equal(ReportingPermissions.MarginRead, permissions["get_margin_summary"]);
        Assert.Equal(ReportingPermissions.PriceChangeRead, permissions["get_price_changes_summary"]);
    }

    // Anthropic acepta a lo sumo 20 herramientas strict por request (24 opcionales, 16 uniones), y las del copiloto crecen con cada módulo.
    // Ninguna escribe (D-C4) y CopilotArguments valida cada argumento, así que van sin strict. Strict quedan las finales.
    [Fact]
    public void NoCopilotToolIsStrict()
    {
        var all = CopilotReportTools.All()
            .Concat(CopilotDocumentTools.All())
            .Concat(CopilotDirectoryTools.All())
            .Concat(CopilotFinanceTools.All());

        Assert.All(all, tool => Assert.False(tool.Contract.Definition.Strict, tool.Contract.Name));
    }

    // El esquema conserva la forma C-8 (todo en required, sin propiedades extra): le dice al modelo qué campos aceptan null.
    [Fact]
    public void EverySchemaIsStrict()
    {
        foreach (var tool in CopilotReportTools.All())
        {
            using var schema = JsonDocument.Parse(tool.Contract.Definition.InputSchemaJson);
            var properties = schema.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order();
            var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order();
            Assert.Equal(properties, required);
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
    }

    [Fact]
    public async Task TheFiltersOfTheModelReachTheQueryButTheTenantIsTheThreads()
    {
        var dispatcher = new CannedDispatcher();
        var client = Guid.NewGuid();

        await Tool("get_orders_summary").ExecuteAsync(
            Call(Arguments(new { from = "2026-10-01", to = "2026-10-31", advisorId = (string?)null, clientId = client.ToString(), paymentStatus = "PaymentPending", source = (string?)null, tenantId = OtherTenant }), dispatcher),
            Ct);

        var filter = Assert.IsType<GetOrdersReportSummaryQuery>(Assert.Single(dispatcher.Queries)).Filter;
        Assert.Equal(new OrdersReportFilter(Tenant, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, client, "PaymentPending", null), filter);
    }

    // D-C3: el alcance lo decide el handler (ScopeAdvisorAsync); la herramienta sólo le dice al modelo cuál fue.
    [Theory]
    [InlineData(false, "own")]
    [InlineData(true, "all")]
    public async Task AnAdvisorWithoutAllAdvisorsGetsItsOwnScope(bool allAdvisors, string scope)
    {
        string[] permissions = allAdvisors ? [ReportingPermissions.OrdersRead, ReportingPermissions.AllAdvisorsRead] : [ReportingPermissions.OrdersRead];

        var result = await Tool("get_orders_summary").ExecuteAsync(Call(EmptyArguments(Tool("get_orders_summary")), new CannedDispatcher(), permissions), Ct);

        using var json = JsonDocument.Parse(result.ResultJson);
        Assert.Equal(scope, json.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task ListsAreCappedAt20WithTotalAndTruncated()
    {
        var dispatcher = new CannedDispatcher { OrdersTotal = 57 };

        var result = await Tool("list_orders_report").ExecuteAsync(Call(EmptyArguments(Tool("list_orders_report")), dispatcher), Ct);

        var query = Assert.IsType<ListOrdersReportQuery>(Assert.Single(dispatcher.Queries));
        Assert.Equal((1, CopilotReportTools.PageSize), (query.Page, query.PageSize));
        using var json = JsonDocument.Parse(result.ResultJson);
        Assert.Equal(20, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(57, json.RootElement.GetProperty("total").GetInt32());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(20, result.Refs.Count);
        Assert.All(result.Refs, reference => Assert.Equal(CopilotCitationKinds.Order, reference.Kind));
        var first = json.RootElement.GetProperty("items")[0].GetProperty("ref");
        Assert.Equal((result.Refs[0].Id, "P-0"), (first.GetProperty("id").GetString(), first.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task TheLastPageIsNotTruncated()
    {
        var dispatcher = new CannedDispatcher { OrdersTotal = 25 };

        var result = await Tool("list_orders_report").ExecuteAsync(
            Call(Arguments(new { from = (string?)null, to = (string?)null, advisorId = (string?)null, clientId = (string?)null, paymentStatus = (string?)null, source = (string?)null, page = 2 }), dispatcher),
            Ct);

        using var json = JsonDocument.Parse(result.ResultJson);
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, Assert.IsType<ListOrdersReportQuery>(Assert.Single(dispatcher.Queries)).Page);
    }

    [Theory]
    [InlineData("2026-13-01", "2026-10-31")]
    [InlineData("2025-01-01", "2026-10-31")]
    [InlineData("2026-10-31", "2026-10-01")]
    public async Task BadDatesAreInvalidArguments(string from, string to)
    {
        await Assert.ThrowsAsync<CopilotArgumentException>(() => Tool("get_margin_summary").ExecuteAsync(
            Call(Arguments(new { from, to, source = (string?)null }), new CannedDispatcher()), Ct));
    }

    [Fact]
    public async Task ABadIdIsAnInvalidArgument()
    {
        await Assert.ThrowsAsync<CopilotArgumentException>(() => Tool("get_orders_summary").ExecuteAsync(
            Call(Arguments(new { from = (string?)null, to = (string?)null, advisorId = "yo", clientId = (string?)null, paymentStatus = (string?)null, source = (string?)null }), new CannedDispatcher()),
            Ct));
    }

    private static ICopilotTool Tool(string name) => CopilotReportTools.All().Single(tool => tool.Contract.Name == name);

    private static CopilotToolCall Call(JsonElement arguments, IRequestDispatcher dispatcher, IReadOnlyCollection<string>? permissions = null) =>
        new(arguments, Principal, dispatcher, permissions ?? [ReportingPermissions.OrdersRead]);

    private static JsonElement Arguments(object value) => JsonSerializer.SerializeToElement(value, Web);

    /// <summary>Todos los campos del esquema en <c>null</c>: lo mínimo que manda un modelo con esquema estricto.</summary>
    private static JsonElement EmptyArguments(ICopilotTool tool)
    {
        using var schema = JsonDocument.Parse(tool.Contract.Definition.InputSchemaJson);
        var values = schema.RootElement.GetProperty("properties").EnumerateObject().ToDictionary(property => property.Name, _ => (object?)null);
        return JsonSerializer.SerializeToElement(values, Web);
    }

    /// <summary>Devuelve una respuesta vacía del tipo que pide cada consulta de reportes y anota lo que le pidieron.</summary>
    private sealed class CannedDispatcher : IRequestDispatcher
    {
        public List<object> Queries { get; } = [];

        public int OrdersTotal { get; init; }

        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A copilot tool never sends a command.");

        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            object response = query switch
            {
                GetOrdersReportSummaryQuery => new OrdersReportSummaryDto(0, [], [], [], [], [], [], [], [], [], new ReportWithoutClientDto(0, []), null),
                ListOrdersReportQuery list => new ReportPage<OrdersReportItemDto>(
                    [
                        .. Enumerable.Range(0, Math.Max(0, Math.Min(list.PageSize, OrdersTotal - ((list.Page - 1) * list.PageSize))))
                            .Select(index => new OrdersReportItemDto(
                                Guid.NewGuid(), $"P-{index}", "Quotation", null, null, DateTimeOffset.UnixEpoch, Guid.NewGuid(), null, Guid.NewGuid(),
                                "Acme", null, "Converted", "PaymentPending", "COP", 1, 0, 0, 0, 1)),
                    ],
                    OrdersTotal, list.Page, list.PageSize),
                GetQuotationsReportSummaryQuery => new QuotationsReportSummaryDto(
                    0, [], [], [], [], [], [], [], [], new QuotationValidityDto(new ReportBucketDto(0, []), new ReportBucketDto(0, []), new ReportBucketDto(0, []), new ReportBucketDto(0, []), 0), [], null),
                ListQuotationsReportQuery list => new ReportPage<QuotationsReportItemDto>([], 0, list.Page, list.PageSize),
                GetCustomerReportSummaryQuery => new CustomerReportSummaryDto(0, 0, [], [], [], null),
                GetMarginReportSummaryQuery => new MarginReportSummaryDto([]),
                GetPriceChangeReportSummaryQuery => new PriceChangeReportSummaryDto(0, 0, 0, 0, [], [], [], null),
                _ => throw new InvalidOperationException($"Unexpected query {query.GetType().Name}."),
            };
            return Task.FromResult((TResponse)response);
        }
    }
}
