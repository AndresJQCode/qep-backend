using Bootstrapper;
using Modules.Messaging.Application;
using Modules.Scheduling.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-23 §2: the state of the AppointmentReminder template of the saved connection. Messaging answers the raw Meta status of the
/// row; no row, or a tenant without the messaging module, is <c>Missing</c>.
/// </summary>
public sealed class SchedulingReminderTemplateStatusTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("APPROVED", null)]
    [InlineData("PENDING", null)]
    [InlineData("REJECTED", "INVALID_FORMAT")]
    [InlineData("PAUSED", null)]
    public async Task TheRawMetaStatusAndReasonTravel(string status, string? reason)
    {
        var port = Port(new StubReadiness(new AppointmentReminderTemplateStatus(status, reason)), TenantModuleKeys.All);

        var state = await port.FindAsync(TenantId, ConnectionId, Ct);

        Assert.Equal(new ReminderTemplateStateDto(status, reason), state);
    }

    [Fact]
    public async Task NoRowIsMissing()
    {
        var state = await Port(new StubReadiness(null), TenantModuleKeys.All).FindAsync(TenantId, ConnectionId, Ct);

        Assert.Equal(new ReminderTemplateStateDto("Missing", null), state);
    }

    [Fact]
    public async Task WithoutTheMessagingModuleItIsMissingAndMessagingIsNotAsked()
    {
        var readiness = new StubReadiness(new AppointmentReminderTemplateStatus("APPROVED", null));
        var withoutMessaging = TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Messaging);

        var state = await Port(readiness, withoutMessaging).FindAsync(TenantId, ConnectionId, Ct);

        Assert.Equal(new ReminderTemplateStateDto("Missing", null), state);
        Assert.Equal(0, readiness.Calls);
    }

    private static SchedulingReminderTemplateStatus Port(StubReadiness readiness, IEnumerable<TenantModuleKey> modules) =>
        new(readiness, new FakeModules(TenantModuleSet.FromStored(modules)));

    private sealed class FakeModules(TenantModuleSet set) : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult<TenantModuleSet?>(set);
    }

    private sealed class StubReadiness(AppointmentReminderTemplateStatus? status) : IAppointmentReminderReadiness
    {
        public int Calls { get; private set; }

        public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The state only reads the status.");

        public Task<AppointmentReminderTemplateStatus?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(status);
        }
    }
}
