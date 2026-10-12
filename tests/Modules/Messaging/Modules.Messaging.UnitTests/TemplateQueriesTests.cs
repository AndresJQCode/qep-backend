using BuildingBlocks.Application;
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §5.2: <c>GET /connections</c> y <c>GET /connections/{id}/templates</c>, ambos con el permiso de lectura de la
/// bandeja y el 403 de siempre ante otro tenant o sin permiso; <c>refresh</c> viaja al catálogo.</summary>
public sealed class TemplateQueriesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheConnectionsAreTheActiveOnesOfTheTenant()
    {
        var bed = new MessagingTestBed();
        bed.Connections.Active.Add(new MessagingConnectionDto(bed.ConnectionId, "Ventas", "+57 300 123 4567"));

        var connections = await bed.ListConnectionsHandler(MessagingPermissions.ConversationRead).HandleAsync(new ListMessagingConnectionsQuery(bed.TenantId), Ct);

        Assert.Equal(new MessagingConnectionDto(bed.ConnectionId, "Ventas", "+57 300 123 4567"), Assert.Single(connections));
    }

    [Fact]
    public async Task TheConnectionsNeedTheReadPermissionOnTheTenantOfTheRoute()
    {
        var bed = new MessagingTestBed();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.ListConnectionsHandler().HandleAsync(new ListMessagingConnectionsQuery(bed.TenantId), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(
            () => bed.ListConnectionsHandler(MessagingPermissions.ConversationRead).HandleAsync(new ListMessagingConnectionsQuery(Guid.CreateVersion7()), Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheTemplatesComeFromTheCatalogAndRefreshTravels(bool refresh)
    {
        var bed = new MessagingTestBed();
        bed.Templates.Catalog.Add(FakeMessageTemplates.Hello());

        var templates = await bed.ListTemplatesHandler(MessagingPermissions.ConversationRead).HandleAsync(new ListTemplatesQuery(bed.TenantId, bed.ConnectionId, refresh), Ct);

        Assert.Equal("hola", Assert.Single(templates).Name);
        Assert.Equal((bed.TenantId, bed.ConnectionId, refresh), Assert.Single(bed.Templates.Calls));
    }

    [Fact]
    public async Task TheTemplatesNeedTheReadPermissionAndNeverReachTheCatalogWithout()
    {
        var bed = new MessagingTestBed();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.ListTemplatesHandler().HandleAsync(new ListTemplatesQuery(bed.TenantId, bed.ConnectionId, false), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(
            () => bed.ListTemplatesHandler(MessagingPermissions.ConversationRead).HandleAsync(new ListTemplatesQuery(Guid.CreateVersion7(), bed.ConnectionId, false), Ct));
        Assert.Empty(bed.Templates.Calls);
    }
}
