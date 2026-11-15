using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Soporte fase 2 (Tarea 6): los adaptadores reales de Bootstrapper contra el esquema migrado. El feed lee toda la base (sin
/// tenant), así que cada prueba levanta su host con el seguimiento apagado: si el worker corriera, se llevaría los entrantes antes de
/// que la prueba los lea.
/// </summary>
public sealed class SupportBootstrapperAdaptersTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Since = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    private QepApiFactory Factory => _factory ?? throw new InvalidOperationException("The host is not started.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => (_database, _factory) = await OpenOwnHostAsync();

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheFeedListsOnlyLiveInboundOfFollowedConversations()
    {
        var tenant = await NewSeededTenantAsync();
        var other = await NewSeededTenantAsync();
        var followed = await SeedConversationAsync(Factory, tenant);
        var unfollowed = await SeedConversationAsync(Factory, tenant, waId: "573009998877");
        var otherConversation = await SeedConversationAsync(Factory, other);
        await SupportTicketSeed.SaveConversationTicketAsync(Factory, tenant, followed, Since);
        // El ticket del otro tenant apunta a la conversación de este tenant: el feed cruza por tenant, así que no la ve seguida.
        await SupportTicketSeed.SaveConversationTicketAsync(Factory, other, followed, Since);

        var live = await SeedMessageAsync(Factory, tenant, followed, Since.AddMinutes(1));
        await SeedMessageAsync(Factory, tenant, followed, Since.AddMinutes(2), direction: Outbound);
        await SeedMessageAsync(Factory, tenant, followed, Since.AddMinutes(3), imported: true);
        await SeedMessageAsync(Factory, tenant, unfollowed, Since.AddMinutes(4));
        await SeedMessageAsync(Factory, other, otherConversation, Since.AddMinutes(5));

        var pending = await FeedAsync();

        var item = Assert.Single(pending);
        Assert.Equal(new SupportPendingInbound(tenant, followed, live, Since.AddMinutes(1)), item);
    }

    [Fact]
    public async Task TheFeedSkipsMessagesAlreadyLinkedOrUnclassified()
    {
        var tenant = await NewSeededTenantAsync();
        var conversation = await SeedConversationAsync(Factory, tenant);
        var ticket = await SupportTicketSeed.SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var linked = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(1));
        var unclassified = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(2));
        var released = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(3));
        var fresh = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(4));

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var links = services.GetRequiredService<ISupportTicketMessageRepository>();
            links.Add(SupportTicketMessage.Link(
                tenant, ticket.Id, conversation, linked, Since.AddMinutes(1), SupportTicketMessageState.Linked, Since));
            var releasedLink = SupportTicketMessage.Link(
                tenant, ticket.Id, conversation, released, Since.AddMinutes(3), SupportTicketMessageState.Pinned, Since);
            releasedLink.Release(Since);
            links.Add(releasedLink);
            services.GetRequiredService<ISupportUnclassifiedRepository>().Add(
                SupportUnclassifiedMessage.Create(tenant, conversation, unclassified, Since.AddMinutes(2), Since));
            await services.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        var pending = await FeedAsync();

        Assert.Equal([fresh], pending.Select(item => item.MessageId));
    }

    [Fact]
    public async Task TheFeedRespectsTheOldestFollowSince()
    {
        var tenant = await NewSeededTenantAsync();
        var conversation = await SeedConversationAsync(Factory, tenant);
        await SupportTicketSeed.SaveConversationTicketAsync(Factory, tenant, conversation, Since.AddHours(1));
        await SupportTicketSeed.SaveConversationTicketAsync(Factory, tenant, conversation, Since.AddHours(3));
        await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(30));
        var afterOldest = await SeedMessageAsync(Factory, tenant, conversation, Since.AddHours(2));
        var afterBoth = await SeedMessageAsync(Factory, tenant, conversation, Since.AddHours(4));

        var pending = await FeedAsync();

        Assert.Equal([afterOldest, afterBoth], pending.Select(item => item.MessageId));
    }

    [Fact]
    public async Task TheConversationEventJoinsTheSupportTransaction()
    {
        var tenant = await NewSeededTenantAsync();
        var conversation = await SeedConversationAsync(Factory, tenant);
        var ticketId = Guid.CreateVersion7();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            await using var transaction = await services.GetRequiredService<ISupportUnitOfWork>().BeginTransactionAsync(Ct);
            await services.GetRequiredService<ISupportConversationEvents>()
                .AppendAsync(tenant, conversation, opened: true, ticketId, null, Since, Ct);
            // Sin confirmar: al liberar la transacción de Soporte se deshace también el evento.
        }

        Assert.Equal(0, await CountTicketEventsAsync(Factory, conversation, ticketId, "TicketOpened"));

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            await using var transaction = await services.GetRequiredService<ISupportUnitOfWork>().BeginTransactionAsync(Ct);
            await services.GetRequiredService<ISupportConversationEvents>()
                .AppendAsync(tenant, conversation, opened: false, ticketId, null, Since, Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(1, await CountTicketEventsAsync(Factory, conversation, ticketId, "TicketClosed"));
    }

    [Fact]
    public async Task TheDirectoryReadsAnInstagramConversationWithoutWaId()
    {
        var tenant = await NewSeededTenantAsync();
        var conversation = await SeedConversationAsync(
            Factory, tenant, channel: "Instagram", waId: null, userId: "17841400000000001", profileName: null, username: "ana.gomez",
            lastInboundAt: Since);
        var message = await SeedMessageAsync(Factory, tenant, conversation, Since, text: "Hola desde Instagram");

        await using var scope = Factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ISupportConversationDirectory>();

        var found = await directory.FindAsync(tenant, conversation, Ct);
        var messages = await directory.FindMessagesAsync(tenant, conversation, [message, Guid.CreateVersion7()], Ct);
        var ofAnotherTenant = await directory.FindAsync(Guid.CreateVersion7(), conversation, Ct);

        Assert.Equal(
            new SupportConversationInfo(conversation, "Instagram", null, null, "ana.gomez", null, false, Since), found);
        var read = Assert.Single(messages);
        Assert.Equal("Inbound", read.Direction);
        Assert.Equal("Text", read.Kind);
        Assert.Equal("Hola desde Instagram", read.Text);
        Assert.Equal(Since, read.OccurredAt);
        Assert.Null(ofAnotherTenant);
    }

    private async Task<Guid> NewSeededTenantAsync()
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(Factory, tenant);
        return Guid.Parse(tenant);
    }

    private async Task<IReadOnlyList<SupportPendingInbound>> FeedAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISupportThreadFeed>().ListPendingAsync(50, Ct);
    }
}
