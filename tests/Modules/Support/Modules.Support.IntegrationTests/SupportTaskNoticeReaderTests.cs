using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Task-notices spec 2026-11-22 §5.7 and D-7: what the escalation worker re-checks right before sending, for a task and for a customer
/// ticket alike. Reads, never writes; a ticket of another tenant is simply not there.
/// </summary>
public sealed class SupportTaskNoticeReaderTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SupportTaskNoticeSnapshot?> ReadAsync(QepApiFactory factory, Guid tenantId, Guid ticketId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISupportTaskNoticeReader>().FindAsync(tenantId, ticketId, Ct);
    }

    [Fact]
    public async Task ReadsAnOpenTaskWithItsAssigneeAndConnection()
    {
        var factory = await host.StartTestAsync();
        var scenario = await SupportTicketScenario.StartAsync(factory);
        var assignee = Guid.CreateVersion7();
        var connection = Guid.CreateVersion7();
        var task = await SupportTicketSeed.SaveInternalTicketAsync(factory, scenario.TenantId, scenario.CallerMemberId, assignee);
        await SetConnectionAsync(factory, scenario.TenantId, connection);

        var snapshot = await ReadAsync(factory, scenario.TenantId, task.Id.Value);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsInternal);
        Assert.True(snapshot.IsOpen);
        Assert.Equal(assignee, snapshot.AssigneeMemberId);
        Assert.Equal(SupportTicketNumber.Format(task.Number), snapshot.TicketNumber);
        Assert.Equal(task.Subject, snapshot.Subject);
        Assert.Equal(connection, snapshot.WhatsAppConnectionId);
    }

    [Fact]
    public async Task ReadsAnOpenCustomerTicket()
    {
        var factory = await host.StartTestAsync();
        var scenario = await SupportTicketScenario.StartAsync(factory);
        var ticket = await scenario.CreateAsync();
        var ticketId = SupportTicketScenario.Id(ticket);

        var snapshot = await ReadAsync(factory, scenario.TenantId, ticketId);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsInternal);
        Assert.True(snapshot.IsOpen);
        Assert.Null(snapshot.AssigneeMemberId);
    }

    [Fact]
    public async Task AResolvedTaskIsNotOpen()
    {
        var factory = await host.StartTestAsync();
        var scenario = await SupportTicketScenario.StartAsync(factory);
        var task = await SupportTicketSeed.SaveInternalTicketAsync(factory, scenario.TenantId, scenario.CallerMemberId, Guid.CreateVersion7());
        await SupportTicketSeed.UpdateTicketAsync(
            factory,
            scenario.TenantId,
            task.Id,
            ticket =>
            {
                ticket.ChangeStatus(SupportTicketStatus.Open, scenario.CallerMemberId, ClockedSupportHost.Reception, SupportTicketSeed.Clock);
                ticket.ChangeStatus(SupportTicketStatus.Resolved, scenario.CallerMemberId, ClockedSupportHost.Reception, SupportTicketSeed.Clock);
            });

        var snapshot = await ReadAsync(factory, scenario.TenantId, task.Id.Value);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsOpen);
    }

    [Fact]
    public async Task AnotherTenantsTicketIsNull()
    {
        var factory = await host.StartTestAsync();
        var scenario = await SupportTicketScenario.StartAsync(factory);
        var task = await SupportTicketSeed.SaveInternalTicketAsync(factory, scenario.TenantId, scenario.CallerMemberId, Guid.CreateVersion7());

        var snapshot = await ReadAsync(factory, Guid.CreateVersion7(), task.Id.Value);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task WithoutSettingsTheConnectionIsNull()
    {
        var factory = await host.StartTestAsync();
        var scenario = await SupportTicketScenario.StartAsync(factory);
        var task = await SupportTicketSeed.SaveInternalTicketAsync(factory, scenario.TenantId, scenario.CallerMemberId, Guid.CreateVersion7());

        var snapshot = await ReadAsync(factory, scenario.TenantId, task.Id.Value);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.WhatsAppConnectionId);
    }

    // The settings row is saved through the domain, as the PUT does, so the read sees the real column.
    private static async Task SetConnectionAsync(QepApiFactory factory, Guid tenantId, Guid connectionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenantId, Ct);
        var settings = await services.GetRequiredService<ISupportSettingsRepository>().GetAsync(tenantId, Ct);
        settings.Update(
            settings.DueSoonBusinessDays,
            settings.FormalNextStep,
            settings.SurveyEnabled,
            settings.AutoCloseBusinessDays,
            settings.UnassignedAlert,
            connectionId,
            ClockedSupportHost.Reception);
        await services.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }
}
