using System.Text.Json;
using Bootstrapper.Copilot;
using BuildingBlocks.Application;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Loans.Application;
using Modules.ServiceContracts.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Plan fase 2, C3.3 (spec 2026-11-17 copiloto §6): préstamos y contratos de servicio. Misma consulta que el endpoint, con los
/// defaults del endpoint; las listas no pasan de 20 filas y llevan el resumen que calcula el backend; los servicios se gatean con la clave
/// de módulo <c>billing</c>.</summary>
public sealed class CopilotFinanceToolsTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly CopilotPrincipal Principal = new(Tenant, Guid.Parse("01900000-0000-7000-8000-0000000000b1"));
    private static readonly Guid LoanId = Guid.Parse("01900000-0000-7000-8000-0000000001a1");
    private static readonly Guid ServiceId = Guid.Parse("01900000-0000-7000-8000-0000000001b1");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, Type> Endpoints() => new()
    {
        { "search_loans", typeof(ListLoansQuery) },
        { "get_loan", typeof(GetLoanQuery) },
        { "list_installments", typeof(ListLoanInstallmentsQuery) },
        { "search_services", typeof(ListServicesQuery) },
        { "get_service", typeof(GetServiceQuery) },
        { "list_charges", typeof(ListChargesQuery) },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task TheSameQueriesAsTheEndpoints(string name, Type query)
    {
        var dispatcher = new CannedDispatcher();
        var tool = Tool(name);

        var result = await tool.ExecuteAsync(Call(Arguments(tool, new { loanId = LoanId.ToString(), serviceId = ServiceId.ToString() }), dispatcher), Ct);

        Assert.False(result.IsError);
        Assert.Equal(query, Assert.Single(dispatcher.Queries).GetType());
        Assert.Equal(Tenant, dispatcher.Queries[0].GetType().GetProperty("TenantId")!.GetValue(dispatcher.Queries[0]));
    }

    [Fact]
    public void TheLoanToolsAskForTheLoansPermissionAndModule()
    {
        Assert.All(
            CopilotFinanceTools.All().Where(tool => tool.Contract.Name is "search_loans" or "get_loan" or "list_installments"),
            tool => Assert.Equal((LoansPermissions.Read, TenantModuleKeys.Loans), (tool.Contract.Permission, tool.Contract.Module)));
    }

    // La clave de módulo de los contratos de servicio sigue siendo «billing» (renombre del 2026-10-30, sólo de código y copy).
    [Fact]
    public void TheBillingModuleKeyGatesTheServiceTools()
    {
        var services = CopilotFinanceTools.All().Where(tool => tool.Contract.Name is "search_services" or "get_service" or "list_charges").ToList();

        Assert.Equal(3, services.Count);
        Assert.All(services, tool =>
        {
            Assert.Equal(ServiceContractsPermissions.Read, tool.Contract.Permission);
            Assert.Equal("billing", tool.Contract.Module.Value);
        });
    }

    [Fact]
    public void EverySchemaIsStrict()
    {
        foreach (var tool in CopilotFinanceTools.All())
        {
            using var schema = JsonDocument.Parse(tool.Contract.Definition.InputSchemaJson);
            var properties = schema.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order();
            var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order();
            Assert.Equal(properties, required);
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
    }

    // Los mismos defaults que el endpoint cuando el modelo no elige estado.
    [Fact]
    public async Task WithoutAStatusTheEndpointDefaultsApply()
    {
        var dispatcher = new CannedDispatcher();

        foreach (var name in new[] { "search_loans", "search_services", "list_charges" })
        {
            await Tool(name).ExecuteAsync(Call(Arguments(Tool(name), new { }), dispatcher), Ct);
        }

        Assert.Equal(ListLoansQuery.DefaultStatus, Assert.IsType<ListLoansQuery>(dispatcher.Queries[0]).Status);
        Assert.Equal(ListServicesQuery.DefaultStatus, Assert.IsType<ListServicesQuery>(dispatcher.Queries[1]).Status);
        var charges = Assert.IsType<ListChargesQuery>(dispatcher.Queries[2]);
        Assert.Equal((ListChargesQuery.DefaultStatus, false, CopilotReportTools.PageSize), (charges.Status, charges.AssigneeMe, charges.PageSize));
    }

    [Theory]
    [InlineData("search_loans", CopilotCitationKinds.Loan)]
    [InlineData("list_installments", CopilotCitationKinds.Loan)]
    [InlineData("search_services", CopilotCitationKinds.Service)]
    [InlineData("list_charges", CopilotCitationKinds.Charge)]
    public async Task ListsAreCappedAt20AndCarryTheBackendSummary(string name, string kind)
    {
        var dispatcher = new CannedDispatcher { Total = 33 };

        var result = await Tool(name).ExecuteAsync(Call(Arguments(Tool(name), new { }), dispatcher), Ct);

        using var json = JsonDocument.Parse(result.ResultJson);
        Assert.Equal((20, 33, true), (json.RootElement.GetProperty("items").GetArrayLength(), json.RootElement.GetProperty("total").GetInt32(), json.RootElement.GetProperty("truncated").GetBoolean()));
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("summary").ValueKind);
        Assert.Equal(20, result.Refs.Count);
        Assert.All(result.Refs, reference => Assert.Equal(kind, reference.Kind));
    }

    [Fact]
    public async Task TheDetailsCiteTheirRecordAndCapTheirRows()
    {
        var dispatcher = new CannedDispatcher { Total = 30 };

        var loan = await Tool("get_loan").ExecuteAsync(Call(Arguments(Tool("get_loan"), new { loanId = LoanId.ToString() }), dispatcher), Ct);
        var service = await Tool("get_service").ExecuteAsync(Call(Arguments(Tool("get_service"), new { serviceId = ServiceId.ToString() }), dispatcher), Ct);

        Assert.Equal(new CopilotCitation(CopilotCitationKinds.Loan, LoanId.ToString(), "Préstamo Acme"), loan.Refs[0]);
        Assert.Contains(loan.Refs, reference => reference.Kind == CopilotCitationKinds.Customer);
        using var loanJson = JsonDocument.Parse(loan.ResultJson);
        Assert.Equal(20, loanJson.RootElement.GetProperty("installments").GetProperty("items").GetArrayLength());
        Assert.True(loanJson.RootElement.GetProperty("installments").GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("3001234567", loan.ResultJson, StringComparison.Ordinal);

        Assert.Equal(new CopilotCitation(CopilotCitationKinds.Service, ServiceId.ToString(), "Hosting"), service.Refs[0]);
        Assert.Equal(20, service.Refs.Count(reference => reference.Kind == CopilotCitationKinds.Charge));
        Assert.DoesNotContain("3001234567", service.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownStatusIsLeftToTheHandlersValidator()
    {
        var dispatcher = new CannedDispatcher();

        await Tool("search_loans").ExecuteAsync(Call(Arguments(Tool("search_loans"), new { status = "settled" }), dispatcher), Ct);

        Assert.Equal("settled", Assert.IsType<ListLoansQuery>(Assert.Single(dispatcher.Queries)).Status);
    }

    private static ICopilotTool Tool(string name) => CopilotFinanceTools.All().Single(tool => tool.Contract.Name == name);

    private static CopilotToolCall Call(JsonElement arguments, IRequestDispatcher dispatcher) =>
        new(arguments, Principal, dispatcher, [LoansPermissions.Read, ServiceContractsPermissions.Read]);

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
        private static readonly LoanCustomerDto LoanCustomer = new(Guid.NewGuid(), "Acme", "+573001234567", "acme@example.com");
        private static readonly CustomerRefDto ServiceCustomer = new(Guid.NewGuid(), "Acme", "+573001234567", "acme@example.com");

        public List<object> Queries { get; } = [];

        public int Total { get; init; }

        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A copilot tool never sends a command.");

        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            object response = query switch
            {
                ListLoansQuery list => new LoanPageDto([.. Rows(list.PageSize).Select(_ => Loan(Guid.NewGuid()))], Total, list.Page, list.PageSize, new LoanSummaryDto(1, 0, 2, [], [], [])),
                GetLoanQuery get => new LoanDetailDto(
                    Loan(get.LoanId),
                    [.. Enumerable.Range(0, Total).Select(index => new LoanInstallmentDto(
                        Guid.NewGuid(), new DateOnly(2024, 1, 1).AddMonths(index), new DateOnly(2024, 1, 28).AddMonths(index), 28, 100, "COP", "Paid", false, null, []))],
                    [], null, null, null, null, false, false),
                ListLoanInstallmentsQuery list => new LoanInstallmentPageDto(
                    [.. Rows(list.PageSize).Select(_ => new LoanInstallmentRowDto(Guid.NewGuid(), Guid.NewGuid(), "Préstamo Acme", LoanCustomer, null, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 100, "COP", false))],
                    Total, list.Page, list.PageSize, new LoanInstallmentSummaryDto(2, [], null)),
                ListServicesQuery list => new ServicePageDto([.. Rows(list.PageSize).Select(_ => Service(Guid.NewGuid()))], Total, list.Page, list.PageSize, new ServiceSummaryDto(1, 0, [], [])),
                GetServiceQuery get => new ServiceDetailDto(
                    Service(get.ServiceId), new ProfileDto(ServiceCustomer.Id, "WhatsApp", new MemberRefDto(Guid.NewGuid(), "Ana", true), 1),
                    [.. Enumerable.Range(0, Total).Select(_ => Charge())], [], []),
                ListChargesQuery list => new ChargePageDto([.. Rows(list.PageSize).Select(_ => Charge())], Total, list.Page, list.PageSize, new ChargeSummaryDto(1, 0, 0, [], [], PriceReviewCountsDto.Empty)),
                _ => throw new InvalidOperationException($"Unexpected query {query.GetType().Name}."),
            };
            return Task.FromResult((TResponse)response);
        }

        private IEnumerable<int> Rows(int pageSize) => Enumerable.Range(0, Math.Min(pageSize, Total));

        private static LoanDto Loan(Guid id) => new(
            id, LoanCustomer, "Préstamo Acme", "InterestOnly", 1000, 1000, "COP", 2, new DateOnly(2024, 1, 1), "EndOfMonth", null, null, "Active",
            1, 100, 10, null, null, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        private static ServiceDto Service(Guid id) => new(
            id, ServiceCustomer, "Hosting", null, 100, "COP", "Monthly", new DateOnly(2024, 1, 1), "Active", null, 0, [], 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new PriceReviewInfoDto("Manual", 0, new DateOnly(2027, 1, 1), null, null, null, "None", null, null, null));

        private static ChargeDto Charge() => new(
            Guid.NewGuid(), Guid.NewGuid(), "Hosting", ServiceCustomer, null, 100, "COP", new DateOnly(2026, 10, 1), "Open", 3, null, null, 1,
            new DateOnly(2026, 10, 1), false);
    }
}
