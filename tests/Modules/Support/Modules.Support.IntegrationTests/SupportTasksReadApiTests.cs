using System.Text.Json;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §5.1 y §5.3 (Task 8): el contrato de lectura de una tarea. Sin cliente, solicitante ni canal de respuesta; con
/// <c>isInternal</c>, el tipo marcado y quién la creó. Las tareas se siembran por el dominio: el alta por HTTP llega con la Task 9.
/// </summary>
public sealed class SupportTasksReadApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(SupportTicketScenario Scenario, Guid Assignee, Guid TaskId)> StartWithTaskAsync()
    {
        var factory = await host.StartTestAsync();
        var scenario = await SupportTicketScenario.StartAsync(factory);
        var assignee = await SeedMemberAsync(
            factory, $"asesor-{Guid.CreateVersion7():N}@example.com", "Aldo Asesor", "advisor", tenantId: scenario.Tenant);
        var task = await SupportTicketSeed.SaveInternalTicketAsync(factory, scenario.TenantId, scenario.CallerMemberId, assignee);
        return (scenario, assignee, task.Id.Value);
    }

    [Fact]
    public async Task TheDetailOfATaskHasNoCustomerRequesterNorReplyChannel()
    {
        var (scenario, assignee, taskId) = await StartWithTaskAsync();

        var detail = await scenario.DetailAsync(taskId);

        Assert.True(detail.GetProperty("isInternal").GetBoolean());
        Assert.Equal("Internal", detail.GetProperty("intake").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("customer").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("requester").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("replyChannel").ValueKind);
        Assert.Equal("Tarea interna", detail.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal(assignee, detail.GetProperty("assignee").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task TheDetailNamesItsCreator()
    {
        var (scenario, _, taskId) = await StartWithTaskAsync();

        var detail = await scenario.DetailAsync(taskId);

        var createdBy = detail.GetProperty("createdBy");
        Assert.Equal(scenario.CallerMemberId, createdBy.GetProperty("id").GetGuid());
        Assert.Equal("Zoe Llamante", createdBy.GetProperty("displayName").GetString());
        Assert.True(createdBy.GetProperty("isMe").GetBoolean());
    }

    [Fact]
    public async Task ATaskNeverOffersWaitingOnCustomer()
    {
        var (scenario, _, taskId) = await StartWithTaskAsync();

        var detail = await scenario.DetailAsync(taskId);

        var allowed = detail.GetProperty("allowedTransitions").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.NotEmpty(allowed);
        Assert.DoesNotContain("WaitingOnCustomer", allowed);
        Assert.Equal(
            SupportTicketStatuses.AllowedFor(SupportTicketStatus.New, isInternal: true).Select(status => status.ToString()),
            allowed);
    }

    [Fact]
    public async Task AManualTicketNamesItsCreatorAndAPublicOneHasNone()
    {
        var factory = await host.StartTestAsync();
        var scenario = await SupportTicketScenario.StartAsync(factory);
        var manual = await scenario.CreateAsync();
        var anonymous = await SupportTicketSeed.SaveAnonymousTicketAsync(factory, scenario.TenantId);

        var manualDetail = await scenario.DetailAsync(SupportTicketScenario.Id(manual));
        var publicDetail = await scenario.DetailAsync(anonymous.Id.Value);

        Assert.False(manualDetail.GetProperty("isInternal").GetBoolean());
        Assert.Equal(scenario.CallerMemberId, manualDetail.GetProperty("createdBy").GetProperty("id").GetGuid());
        Assert.Equal("Email", manualDetail.GetProperty("replyChannel").GetString());
        Assert.False(publicDetail.GetProperty("isInternal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, publicDetail.GetProperty("createdBy").ValueKind);
    }

    [Fact]
    public async Task TheTypeSelectorListsTheInternalTypeLastAndMarked()
    {
        var factory = await host.StartTestAsync();
        var scenario = await SupportTicketScenario.StartAsync(factory);

        var types = (await JsonAsync(await scenario.Client.GetAsync($"{scenario.Url}/ticket-types", Ct))).EnumerateArray().ToList();

        Assert.Equal(9, types.Count);
        Assert.True(types[^1].GetProperty("isInternal").GetBoolean());
        Assert.Equal("Tarea interna", types[^1].GetProperty("name").GetString());
        Assert.All(types.Take(8), type => Assert.False(type.GetProperty("isInternal").GetBoolean()));
        var settings = (await JsonAsync(await scenario.Client.GetAsync($"{scenario.Url}/settings/ticket-types", Ct))).EnumerateArray().ToList();
        Assert.Equal(8, settings.Count);
        Assert.All(settings, type => Assert.False(type.GetProperty("isInternal").GetBoolean()));
    }
}
