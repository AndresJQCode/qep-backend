using System.Reflection;
using System.Text.Json;
using Bootstrapper.Copilot;
using BuildingBlocks.Application;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Catalog.Application;
using Modules.Customers.Application;
using Modules.Messaging.Application;

namespace Bootstrapper.UnitTests;

/// <summary>Plan fase 2, C3.2 (spec 2026-11-17 copiloto §6 y D-C10): clientes, catálogo y el resumen de Mensajería. Misma consulta que el
/// endpoint; la ficha del cliente no trae notas; el costo sólo si el handler lo trajo; el resumen de Mensajería son sólo agregados.</summary>
public sealed class CopilotDirectoryToolsTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly CopilotPrincipal Principal = new(Tenant, Guid.Parse("01900000-0000-7000-8000-0000000000b1"));
    private static readonly Guid CustomerId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid ProductId = Guid.Parse("01900000-0000-7000-8000-0000000000f1");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, Type, string, string> Endpoints() => new()
    {
        { "search_customers", typeof(ListCustomersQuery), CustomersPermissions.CustomerRead, "customers" },
        { "get_customer", typeof(GetCustomerQuery), CustomersPermissions.CustomerRead, "customers" },
        { "search_products", typeof(ListProductsQuery), CatalogPermissions.ProductRead, "catalog" },
        { "get_product", typeof(GetProductQuery), CatalogPermissions.ProductRead, "catalog" },
        { "get_messaging_summary", typeof(GetMessagingReportSummaryQuery), MessagingPermissions.ConversationRead, "messaging" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task TheSameQueriesAsTheEndpoints(string name, Type query, string permission, string module)
    {
        var dispatcher = new CannedDispatcher();
        var tool = Tool(name);

        var result = await tool.ExecuteAsync(
            Call(Arguments(tool, new { customerId = CustomerId.ToString(), productId = ProductId.ToString(), from = "2026-10-01", to = "2026-10-31", groupBy = "week" }), dispatcher),
            Ct);

        Assert.False(result.IsError);
        Assert.Equal(query, Assert.Single(dispatcher.Queries).GetType());
        Assert.Equal(permission, tool.Contract.Permission);
        Assert.Equal(module, tool.Contract.Module.Value);
        Assert.Equal(Tenant, dispatcher.Queries[0].GetType().GetProperty("TenantId")!.GetValue(dispatcher.Queries[0]));
    }

    [Fact]
    public void EverySchemaIsStrict()
    {
        foreach (var tool in CopilotDirectoryTools.All())
        {
            using var schema = JsonDocument.Parse(tool.Contract.Definition.InputSchemaJson);
            var properties = schema.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order();
            var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order();
            Assert.Equal(properties, required);
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
    }

    // D-C10 (5): el MVP no lee notas de clientes. La ficha tampoco lleva las direcciones ni el consentimiento.
    [Fact]
    public async Task TheCustomerSheetNeverCarriesNotes()
    {
        var result = await Tool("get_customer").ExecuteAsync(
            Call(Arguments(Tool("get_customer"), new { customerId = CustomerId.ToString() }), new CannedDispatcher()), Ct);

        Assert.DoesNotContain("note", result.ResultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bodega", result.ResultJson, StringComparison.Ordinal);
        Assert.Contains("Acme SAS", result.ResultJson, StringComparison.Ordinal);
        Assert.Equal(new CopilotCitation(CopilotCitationKinds.Customer, CustomerId.ToString(), "Acme SAS"), Assert.Single(result.Refs));
        Assert.DoesNotContain(typeof(CopilotDirectoryTools).Assembly.GetTypes(), type =>
            type.Namespace == "Bootstrapper.Copilot" && type.Name.Contains("Note", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACustomerListIsCappedAt20()
    {
        var dispatcher = new CannedDispatcher { Total = 45 };

        var result = await Tool("search_customers").ExecuteAsync(Call(Arguments(Tool("search_customers"), new { search = "acme" }), dispatcher), Ct);

        var query = Assert.IsType<ListCustomersQuery>(Assert.Single(dispatcher.Queries));
        Assert.Equal(("acme", 1, CopilotReportTools.PageSize), (query.Search, query.Page, query.PageSize));
        using var json = JsonDocument.Parse(result.ResultJson);
        Assert.Equal((20, 45, true), (json.RootElement.GetProperty("items").GetArrayLength(), json.RootElement.GetProperty("total").GetInt32(), json.RootElement.GetProperty("truncated").GetBoolean()));
        Assert.All(result.Refs, reference => Assert.Equal(CopilotCitationKinds.Customer, reference.Kind));
    }

    // §6: el costo sólo si el handler lo trae (CatalogCostAccess); sin permiso llega null y así sigue.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProductWithoutCostPermissionHasNoCost(bool withCost)
    {
        var dispatcher = new CannedDispatcher { WithCost = withCost };

        var result = await Tool("get_product").ExecuteAsync(
            Call(Arguments(Tool("get_product"), new { productId = ProductId.ToString() }), dispatcher), Ct);

        using var json = JsonDocument.Parse(result.ResultJson);
        var costs = json.RootElement.GetProperty("variants").GetProperty("items")[0].GetProperty("costs");
        if (withCost)
        {
            Assert.Equal(4321m, costs.GetProperty("COP").GetDecimal());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, costs.ValueKind);
            Assert.DoesNotContain("4321", result.ResultJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AProductCarriesAtMost50Variants()
    {
        var result = await Tool("get_product").ExecuteAsync(
            Call(Arguments(Tool("get_product"), new { productId = ProductId.ToString() }), new CannedDispatcher { Variants = 70 }), Ct);

        using var json = JsonDocument.Parse(result.ResultJson);
        var variants = json.RootElement.GetProperty("variants");
        Assert.Equal((50, 70, true), (variants.GetProperty("items").GetArrayLength(), variants.GetProperty("total").GetInt32(), variants.GetProperty("truncated").GetBoolean()));
        Assert.DoesNotContain("fileId", result.ResultJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new CopilotCitation(CopilotCitationKinds.Product, ProductId.ToString(), "Camiseta"), Assert.Single(result.Refs));
    }

    // D-C10 (5): ningún campo del resumen puede traer texto de una conversación: se mira la forma del DTO, no un ejemplo.
    [Fact]
    public async Task TheMessagingSummaryHasNoMessageText()
    {
        var result = await Tool("get_messaging_summary").ExecuteAsync(
            Call(Arguments(Tool("get_messaging_summary"), new { from = "2026-10-01", to = "2026-10-31", groupBy = "week" }), new CannedDispatcher()), Ct);

        Assert.False(result.IsError);
        var forbidden = new[] { "Text", "Body", "Content", "Message", "Caption", "Snippet", "Preview", "Note" };
        Assert.DoesNotContain(PropertiesOf(typeof(MessagingReportSummaryDto)), name => forbidden.Any(word => name.Contains(word, StringComparison.Ordinal)));
    }

    private static IEnumerable<string> PropertiesOf(Type type, HashSet<Type>? seen = null)
    {
        seen ??= [];
        if (!seen.Add(type) || type.Namespace?.StartsWith("Modules.", StringComparison.Ordinal) != true)
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return property.Name;
            var inner = property.PropertyType.IsGenericType ? property.PropertyType.GetGenericArguments()[0] : property.PropertyType;
            foreach (var name in PropertiesOf(inner, seen))
            {
                yield return name;
            }
        }
    }

    private static ICopilotTool Tool(string name) => CopilotDirectoryTools.All().Single(tool => tool.Contract.Name == name);

    private static CopilotToolCall Call(JsonElement arguments, IRequestDispatcher dispatcher) =>
        new(arguments, Principal, dispatcher, [CustomersPermissions.CustomerRead, CatalogPermissions.ProductRead, MessagingPermissions.ConversationRead]);

    private static JsonElement Arguments(ICopilotTool tool, object values)
    {
        using var schema = JsonDocument.Parse(tool.Contract.Definition.InputSchemaJson);
        var given = JsonSerializer.SerializeToElement(values, Web);
        var arguments = schema.RootElement.GetProperty("properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => given.TryGetProperty(property.Name, out var value) ? (object?)value : null);
        return JsonSerializer.SerializeToElement(arguments, Web);
    }

    private sealed class CannedDispatcher : IRequestDispatcher
    {
        public List<object> Queries { get; } = [];

        public int Total { get; init; }

        public bool WithCost { get; init; }

        public int Variants { get; init; } = 1;

        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A copilot tool never sends a command.");

        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            object response = query switch
            {
                ListCustomersQuery list => new CustomerPage(
                    [.. Enumerable.Range(0, Math.Max(0, Math.Min(list.PageSize, Total))).Select(_ => Customer(Guid.NewGuid()))], Total, list.Page, list.PageSize),
                GetCustomerQuery get => Customer(get.CustomerId),
                ListProductsQuery list => new ProductPage([], 0, list.Page, list.PageSize),
                GetProductQuery => Product(),
                GetMessagingReportSummaryQuery => JsonSerializer.Deserialize<MessagingReportSummaryDto>("{}", Web)!,
                _ => throw new InvalidOperationException($"Unexpected query {query.GetType().Name}."),
            };
            return Task.FromResult((TResponse)response);
        }

        private static CustomerDto Customer(Guid id) => JsonSerializer.Deserialize<CustomerDto>(
            $$$"""
            {"id":"{{{id}}}","cuc":"CUC-1","name":"Ana","businessName":"Acme SAS","phone":"3001234567","isActive":true,"isComplete":true,
             "addresses":[{"id":"{{{Guid.NewGuid()}}}","name":"Bodega Norte","address":"Calle 1","cityName":"Bogotá","isPrincipal":true}],
             "tags":[{"id":"{{{Guid.NewGuid()}}}","name":"VIP","color":"red","customerCount":1,"version":1}],
             "marketingConsent":{"optedOut":false}}
            """,
            Web)!;

        private ProductDto Product() => JsonSerializer.Deserialize<ProductDto>(
            $$$"""
            {"id":"{{{ProductId}}}","name":"Camiseta","isActive":true,"description":"Algodón","imageFileId":"{{{Guid.NewGuid()}}}",
             "options":[],"gallery":[{"id":"{{{Guid.NewGuid()}}}","fileId":"{{{Guid.NewGuid()}}}"}],
             "variants":[{{{string.Join(',', Enumerable.Range(0, Variants).Select(index =>
                 $$"""{"id":"{{Guid.NewGuid()}}","name":"Camiseta {{index}}","code":"CAM-{{index}}","optionValueIds":[],"prices":{"COP":10000},"priceScales":[],"packagingUnits":[],"galleryFileId":"{{Guid.NewGuid()}}","isActive":true,"canDelete":true,"costs":{{(WithCost ? "{\"COP\":4321}" : "null")}}}"""))}}}]}
            """,
            Web)!;
    }
}
