using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 §4.4: <c>GET /runs?chatbotId=&amp;from=&amp;to=</c>, el historial de corridas de un chatbot para la lista.</summary>
public sealed class RunsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RunsUrl(Guid tenantId, Guid chatbotId, string query = "") =>
        $"{AutomationsUrl(tenantId)}/runs?chatbotId={chatbotId}{query}";

    private static ConversationRun NewRun(Guid tenantId, Guid chatbotId, RunStatus status, DateTimeOffset startedAt, TimeSpan? endsAfter = null)
    {
        var run = ConversationRun.Start(
            Guid.CreateVersion7(), tenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), chatbotId, Guid.CreateVersion7(),
            new RunState(RunStatus.Waiting, "n1", 3, 0, RunVariables.Empty(), []), startedAt);
        if (endsAfter is { } duration)
        {
            run.Finish(status, status == RunStatus.Failed ? "automations.run.version_missing" : null, startedAt + duration);
        }

        return run;
    }

    private static async Task SeedAsync(QepApiFactory factory, params ConversationRun[] runs)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConversationRunRepository>();
        foreach (var run in runs)
        {
            await repository.AddAsync(run, Ct);
        }

        await scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ListsTheRunsOfAChatbotInTheRange()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var chatbotId = Guid.CreateVersion7();
        var t0 = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var older = NewRun(tenant.TenantId, chatbotId, RunStatus.Failed, t0, TimeSpan.FromSeconds(90));
        var newer = NewRun(tenant.TenantId, chatbotId, RunStatus.Waiting, t0.AddHours(2));
        var outside = NewRun(tenant.TenantId, chatbotId, RunStatus.Completed, t0.AddDays(-30), TimeSpan.FromSeconds(5));
        var otherChatbot = NewRun(tenant.TenantId, Guid.CreateVersion7(), RunStatus.Waiting, t0.AddHours(1));
        await SeedAsync(factory, older, newer, outside, otherChatbot);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var from = Uri.EscapeDataString(t0.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(t0.AddDays(1).ToString("O"));
        var runs = await client.GetFromJsonAsync<List<RunDto>>(RunsUrl(tenant.TenantId, chatbotId, $"&from={from}&to={to}"), Ct);

        Assert.NotNull(runs);
        Assert.Equal([newer.Id, older.Id], runs.Select(run => run.Id));
        Assert.Equal(("Waiting", "n1", 3, (int?)null, (string?)null), (runs[0].Status, runs[0].CurrentNodeId, runs[0].StepNumber, runs[0].DurationSeconds, runs[0].FailureCode));
        Assert.Equal(("Failed", 90, "automations.run.version_missing"), (runs[1].Status, runs[1].DurationSeconds, runs[1].FailureCode));
        Assert.NotNull(runs[1].EndedAt);
    }

    [Fact]
    public async Task TheDefaultRangeIsTheLastSevenDays()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var chatbotId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var recent = NewRun(tenant.TenantId, chatbotId, RunStatus.Waiting, now.AddDays(-2));
        var old = NewRun(tenant.TenantId, chatbotId, RunStatus.Completed, now.AddDays(-10), TimeSpan.FromSeconds(5));
        await SeedAsync(factory, recent, old);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var runs = await client.GetFromJsonAsync<List<RunDto>>(RunsUrl(tenant.TenantId, chatbotId), Ct);

        Assert.Equal([recent.Id], runs!.Select(run => run.Id));
    }

    [Fact]
    public async Task AnotherTenantIs403AndOtherTenantRunsAreNotListed()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        await EnableAutomationsAsync(connectionString, other.TenantId);
        var chatbotId = Guid.CreateVersion7();
        await SeedAsync(factory, NewRun(tenant.TenantId, chatbotId, RunStatus.Waiting, DateTimeOffset.UtcNow.AddHours(-1)));
        var otherClient = CreateClient(factory, other.OwnerUserId, other.TenantId, ReadPermissions);

        var cross = await otherClient.GetAsync(RunsUrl(tenant.TenantId, chatbotId), Ct);
        var own = await otherClient.GetFromJsonAsync<List<RunDto>>(RunsUrl(other.TenantId, chatbotId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        Assert.Empty(own!);
    }
}
