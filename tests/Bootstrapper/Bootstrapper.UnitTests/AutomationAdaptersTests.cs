using Bootstrapper;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-21 §5.3: los adaptadores que le dan a Automations lo de Mensajería e Integrations. El de nombres de
/// chatbots lee <c>AutomationsDbContext</c> y se prueba contra PostgreSQL en <c>MessageAuthorshipApiTests.TheThreadShowsTheRealChatbotName</c>
/// (el repo no usa EF en memoria).</summary>
public sealed class AutomationAdaptersTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListsWhatsAppAndInstagramWithHandles()
    {
        var whatsApp = Guid.CreateVersion7();
        var instagram = Guid.CreateVersion7();
        var paused = Guid.CreateVersion7();
        var directory = new StubDirectory(
            all:
            [
                new MessagingConnectionRef(whatsApp, "Ventas", ConversationChannel.WhatsApp, "Active"),
                new MessagingConnectionRef(paused, "Soporte", ConversationChannel.WhatsApp, "Paused"),
                new MessagingConnectionRef(instagram, "Tienda", ConversationChannel.Instagram, "Active"),
            ],
            active:
            [
                new MessagingConnectionDto(whatsApp, "Ventas", "+57 300 1234567"),
                new MessagingConnectionDto(instagram, "Tienda", null, nameof(ConversationChannel.Instagram), "origenbotanico"),
            ]);

        var result = await new AutomationConnections(directory).ListAsync(TenantId, Ct);

        Assert.Equal(
            [
                (whatsApp, "WhatsApp", "+57 300 1234567", "Active"),
                (paused, "WhatsApp", string.Empty, "Paused"),
                (instagram, "Instagram", "@origenbotanico", "Active"),
            ],
            result.Select(connection => (connection.Id, connection.Channel, connection.Handle, connection.Status)));
    }

    [Fact]
    public async Task ExternalApisComeFromTheExternalApiProvider()
    {
        var active = Guid.CreateVersion7();
        var paused = Guid.CreateVersion7();
        var connections = new StubIntegrationConnections(
            [new ConnectionListing(active, "Clientes", ConnectionStatus.Active), new ConnectionListing(paused, "Órdenes", ConnectionStatus.Paused)]);

        var result = await new AutomationExternalApis(connections).ListAsync(TenantId, Ct);

        Assert.Equal("external-api", connections.RequestedProvider);
        Assert.Equal([(active, "Clientes", "Active"), (paused, "Órdenes", "Paused")], result.Select(api => (api.Id, api.Name, api.Status)));
    }

    [Fact]
    public async Task ScheduleNamesCarryTheEffectiveScheduleOrNull()
    {
        var withSchedule = Guid.CreateVersion7();
        var without = Guid.CreateVersion7();
        var hours = new StubBusinessHours(new Dictionary<Guid, BusinessHoursStatus>
        {
            [withSchedule] = new(true, null, Guid.CreateVersion7(), "Horario comercial"),
            [without] = new(true, null, null, null),
        });

        var result = await new AutomationBusinessHours(hours).ScheduleNamesAsync(TenantId, [withSchedule, without], Ct);

        Assert.Equal("Horario comercial", result[withSchedule]);
        Assert.Null(result[without]);
    }

    private sealed class StubDirectory(IReadOnlyList<MessagingConnectionRef> all, IReadOnlyList<MessagingConnectionDto> active) : IMessagingConnectionDirectory
    {
        public Task<IReadOnlyList<MessagingConnectionRef>> ListAllAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(all);

        public Task<IReadOnlyList<MessagingConnectionDto>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(active);

        public Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MessagingRoute>> FindByAccountAsync(string wabaId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessagingSender?> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessagingTemplateSource?> ResolveTemplateSourceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessagingRoute?> FindInstagramRouteAsync(string igUserId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstagramSender?> ResolveInstagramSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConversationChannel?> FindChannelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubIntegrationConnections(IReadOnlyList<ConnectionListing> listings) : IIntegrationConnections
    {
        public string? RequestedProvider { get; private set; }

        public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken)
        {
            RequestedProvider = providerKey;
            return Task.FromResult(listings);
        }

        public Task<ResolvedConnection?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubBusinessHours(IReadOnlyDictionary<Guid, BusinessHoursStatus> statuses) : IBusinessHours
    {
        public Task<BusinessHoursStatus> GetStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, BusinessHoursStatus>> GetStatusesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) => Task.FromResult(statuses);
    }
}
