using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Soporte fase 2 (Tarea 8, plan E2 a E6): el seguimiento del hilo. Host propio por prueba con el worker apagado: la prueba llama
/// <see cref="SupportThreadFollower.ProcessBatchAsync(CancellationToken)"/> a mano, y el feed lee toda la base.
/// </summary>
public sealed class SupportThreadFollowerTests : IAsyncLifetime
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
    public async Task ASingleFollowingTicketLinksTheInbound()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var ticket = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var message = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(5));

        await ProcessAsync();

        Assert.Equal("Linked", await LinkStateAsync(ticket.Id, message));
        Assert.Equal(0, await UnclassifiedCountAsync(conversation));
    }

    // Review Focus 1: Meta reintenta horas después; la hora del mensaje es anterior al ticket aunque su llegada sea posterior.
    [Fact]
    public async Task AnInboundOlderThanTheTicketIsNotLinked()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var ticket = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var late = await SeedMessageAsync(Factory, tenant, conversation, Since.AddHours(-1));

        await ProcessAsync();

        Assert.Null(await LinkStateAsync(ticket.Id, late));
        Assert.Equal(0, await UnclassifiedCountAsync(conversation));
    }

    [Fact]
    public async Task TwoFollowingTicketsLeaveTheInboundUnclassified()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var first = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var second = await SaveConversationTicketAsync(Factory, tenant, conversation, Since.AddMinutes(1));
        var message = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(5));

        await ProcessAsync();

        Assert.Null(await LinkStateAsync(first.Id, message));
        Assert.Null(await LinkStateAsync(second.Id, message));
        Assert.Equal(1, await UnclassifiedCountAsync(conversation));
    }

    // Plan E4: lo que llegó con el interruptor apagado no se vincula, ni siquiera al prenderlo de nuevo.
    [Fact]
    public async Task AnInboundWhileFollowIsOffIsNotLinked()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var ticket = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var actor = Guid.CreateVersion7();
        await UpdateTicketAsync(Factory, tenant, ticket.Id, value => value.SetFollowThread(false, actor, Since.AddHours(1)));
        var whileOff = await SeedMessageAsync(Factory, tenant, conversation, Since.AddHours(2));
        await UpdateTicketAsync(Factory, tenant, ticket.Id, value => value.SetFollowThread(true, actor, Since.AddHours(3)));
        var afterOn = await SeedMessageAsync(Factory, tenant, conversation, Since.AddHours(4));

        await ProcessAsync();

        Assert.Null(await LinkStateAsync(ticket.Id, whileOff));
        Assert.Equal("Linked", await LinkStateAsync(ticket.Id, afterOn));
    }

    [Fact]
    public async Task ALinkedInboundReopensATicketWaitingOnTheCustomer()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var ticket = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        await UpdateTicketAsync(
            Factory, tenant, ticket.Id,
            value => value.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Guid.CreateVersion7(), Since.AddMinutes(1), Clock));
        var message = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(5));

        await ProcessAsync();

        var reopened = await FindTicketAsync(Factory, tenant, ticket.Id);
        Assert.Equal(SupportTicketStatus.Open, reopened.Status);
        Assert.Null(reopened.PausedSince);
        Assert.Equal("Linked", await LinkStateAsync(ticket.Id, message));
        var last = (await EventsAsync(tenant, ticket.Id))[^1];
        Assert.Equal(SupportTicketEventKind.StatusChanged, last.Kind);
        Assert.Null(last.ActorMemberId);
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketStatusChanged, ticket.Id.ToString()));
    }

    // Plan E6 (decisión pendiente P4): un resuelto no se reabre solo con la respuesta del cliente.
    [Fact]
    public async Task ALinkedInboundDoesNotReopenAResolvedTicket()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var ticket = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var resolved = await UpdateTicketAsync(
            Factory, tenant, ticket.Id,
            value => value.ChangeStatus(SupportTicketStatus.Resolved, Guid.CreateVersion7(), Since.AddMinutes(1), Clock));
        var message = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(5));

        await ProcessAsync();

        var after = await FindTicketAsync(Factory, tenant, ticket.Id);
        Assert.Equal(SupportTicketStatus.Resolved, after.Status);
        Assert.Equal(resolved.Version, after.Version);
        Assert.Equal("Linked", await LinkStateAsync(ticket.Id, message));
    }

    // Review Focus 2: dos pasadas seguidas y dos réplicas a la vez sobre el mismo entrante dejan un solo vínculo y ningún 500.
    [Fact]
    public async Task ProcessingTheSameInboundTwiceLinksItOnce()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var ticket = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var first = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(1));

        await ProcessAsync();
        await ProcessAsync();

        var second = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(2));
        await ConcurrentAsync();

        Assert.Equal(1, await LinkCountAsync(ticket.Id, first));
        Assert.Equal(1, await LinkCountAsync(ticket.Id, second));

        // Dos tickets: el mismo entrante sin clasificar, de dos réplicas a la vez, deja una sola fila.
        var other = await SaveConversationTicketAsync(Factory, tenant, conversation, Since.AddMinutes(3));
        var third = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(4));
        await ConcurrentAsync();
        await ProcessAsync();

        Assert.Equal(1, await UnclassifiedCountAsync(conversation));
        Assert.Null(await LinkStateAsync(other.Id, third));
    }

    // Review Focus 5: cerrar uno de los dos tickets deja al otro como único candidato, y la fila pendiente pasa sola a él.
    [Fact]
    public async Task AClosedTicketStopsFollowingAndLeavesASingleCandidate()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var closing = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var staying = await SaveConversationTicketAsync(Factory, tenant, conversation, Since.AddMinutes(1));
        var pending = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(5));
        await ProcessAsync();
        Assert.Equal(1, await UnclassifiedCountAsync(conversation));

        var actor = Guid.CreateVersion7();
        await UpdateTicketAsync(Factory, tenant, closing.Id, value =>
        {
            value.ChangeStatus(SupportTicketStatus.Resolved, actor, Since.AddMinutes(10), Clock);
            value.ChangeStatus(SupportTicketStatus.Closed, actor, Since.AddMinutes(11), Clock);
        });
        var afterClose = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(20));

        await ProcessAsync();

        var closed = await FindTicketAsync(Factory, tenant, closing.Id);
        Assert.False(closed.FollowThread);
        Assert.Equal(Since.AddMinutes(11), closed.FollowOffAt);
        Assert.Equal(0, await UnclassifiedCountAsync(conversation));
        Assert.Equal(
            "Reassigned",
            await TextAsync(Factory, "SELECT resolution FROM support.unclassified_messages WHERE message_id = @id", ("id", pending)));
        Assert.Equal("Linked", await LinkStateAsync(staying.Id, pending));
        Assert.Equal("Linked", await LinkStateAsync(staying.Id, afterClose));
        Assert.Null(await LinkStateAsync(closing.Id, afterClose));
    }

    [Fact]
    public async Task AnAutomaticLinkDoesNotBumpTheTicketVersion()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var ticket = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var message = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(5));

        await ProcessAsync();

        Assert.Equal("Linked", await LinkStateAsync(ticket.Id, message));
        var after = await FindTicketAsync(Factory, tenant, ticket.Id);
        Assert.Equal(ticket.Version, after.Version);
        Assert.Single(await EventsAsync(tenant, ticket.Id));
    }

    [Fact]
    public async Task EchoesAndImportedMessagesAreNeverLinked()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var ticket = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var echo = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(1), direction: Outbound);
        var imported = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(2), imported: true);

        var processed = await ProcessAsync();

        Assert.Equal(0, processed);
        Assert.Null(await LinkStateAsync(ticket.Id, echo));
        Assert.Null(await LinkStateAsync(ticket.Id, imported));
        Assert.Equal(0, await UnclassifiedCountAsync(conversation));
    }

    private async Task<(Guid Tenant, Guid Conversation)> NewConversationAsync()
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(Factory, tenant);
        var tenantId = Guid.Parse(tenant);
        return (tenantId, await SeedConversationAsync(Factory, tenantId));
    }

    private async Task<int> ProcessAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SupportThreadFollower>().ProcessBatchAsync(Ct);
    }

    // Dos réplicas: dos scopes con su propio contexto y su propia conexión, a la vez.
    private async Task<int> ConcurrentAsync()
    {
        var results = await Task.WhenAll(ProcessAsync(), ProcessAsync());
        return results.Sum();
    }

    private Task<string?> LinkStateAsync(SupportTicketId ticketId, Guid messageId) =>
        TextAsync(
            Factory,
            "SELECT state FROM support.ticket_messages WHERE ticket_id = @ticket AND message_id = @message",
            ("ticket", ticketId.Value), ("message", messageId));

    private Task<long> LinkCountAsync(SupportTicketId ticketId, Guid messageId) =>
        ScalarAsync(
            Factory,
            "SELECT count(*) FROM support.ticket_messages WHERE ticket_id = @ticket AND message_id = @message",
            ("ticket", ticketId.Value), ("message", messageId));

    private Task<long> UnclassifiedCountAsync(Guid conversation) =>
        ScalarAsync(
            Factory,
            "SELECT count(*) FROM support.unclassified_messages WHERE conversation_id = @id AND resolution IS NULL",
            ("id", conversation));

    private async Task<IReadOnlyList<SupportTicketEvent>> EventsAsync(Guid tenant, SupportTicketId ticketId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().ListEventsAsync(tenant, ticketId, Ct);
    }
}
