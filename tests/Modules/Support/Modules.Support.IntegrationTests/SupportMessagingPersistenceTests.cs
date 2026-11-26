using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// El esquema de la fase 2 (migración <c>AddSupportMessaging</c>): la conversación y la ventana de seguimiento del ticket, los
/// vínculos de mensajes, los entrantes sin clasificar, las respuestas y la traducción por nombre de índice de sus únicos (plan E3
/// y E16). Host y base propios de la clase (<see cref="SharedSupportHost"/> es uno por clase): cada prueba siembra su tenant y
/// sólo lee lo de ese tenant.
/// </summary>
public sealed class SupportMessagingPersistenceTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(new WeekdayCalendar(), TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person =
        new("Ana", "Gómez", null, null, null, "ana@example.com", "+573001234567", null, "CO", null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATicketFromAConversationRoundTripsItsFollowWindow()
    {
        var (factory, tenant) = await NewTenantAsync();
        var conversation = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();
        var ticket = await FromConversationAsync(factory, tenant, 1, conversation, "Instagram");
        ticket.SetFollowThread(false, actor, Now.AddHours(2));
        await SaveAsync(factory, ticket);

        await using var read = factory.Services.CreateAsyncScope();
        var found = await read.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticket.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(SupportTicketIntake.Conversation, found.Intake);
        Assert.Equal(SupportReplyChannel.Thread, found.ReplyChannel);
        Assert.Null(found.ManualMedium);
        Assert.Equal(conversation, found.ConversationId);
        Assert.Equal("Instagram", found.ConversationChannel);
        Assert.False(found.FollowThread);
        Assert.Equal(Now, found.FollowSince);
        Assert.Equal(Now.AddHours(2), found.FollowOffAt);
        Assert.Equal(2L, found.Version);
    }

    [Fact]
    public async Task TheSameMessageCannotBeLinkedTwiceToATicket()
    {
        var (factory, tenant) = await NewTenantAsync();
        var conversation = Guid.CreateVersion7();
        var ticket = await FromConversationAsync(factory, tenant, 1, conversation);
        await SaveAsync(factory, ticket);
        var message = Guid.CreateVersion7();
        await LinkAsync(factory, tenant, ticket.Id, conversation, message, Now.AddMinutes(1), SupportTicketMessageState.Linked);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => LinkAsync(factory, tenant, ticket.Id, conversation, message, Now.AddMinutes(1), SupportTicketMessageState.Pinned));

        Assert.Equal(SupportErrorCodes.MessageAlreadyLinked, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        var found = await read.ServiceProvider.GetRequiredService<ISupportTicketMessageRepository>()
            .FindAsync(tenant, ticket.Id, message, Ct);
        Assert.Equal(SupportTicketMessageState.Linked, found!.State);
    }

    [Fact]
    public async Task VisibleMessagesSkipReleasedAndPageByCursor()
    {
        var (factory, tenant) = await NewTenantAsync();
        var conversation = Guid.CreateVersion7();
        var ticket = await FromConversationAsync(factory, tenant, 1, conversation);
        await SaveAsync(factory, ticket);
        var oldest = Guid.CreateVersion7();
        var pinned = Guid.CreateVersion7();
        var released = Guid.CreateVersion7();
        var newest = Guid.CreateVersion7();
        await LinkAsync(factory, tenant, ticket.Id, conversation, oldest, Now.AddMinutes(1), SupportTicketMessageState.Linked);
        await LinkAsync(factory, tenant, ticket.Id, conversation, pinned, Now.AddMinutes(2), SupportTicketMessageState.Pinned);
        await LinkAsync(factory, tenant, ticket.Id, conversation, released, Now.AddMinutes(3), SupportTicketMessageState.Linked);
        await LinkAsync(factory, tenant, ticket.Id, conversation, newest, Now.AddMinutes(4), SupportTicketMessageState.Linked);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var link = await scope.ServiceProvider.GetRequiredService<ISupportTicketMessageRepository>()
                .FindAsync(tenant, ticket.Id, released, Ct);
            link!.Release(Now.AddMinutes(5));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var links = read.ServiceProvider.GetRequiredService<ISupportTicketMessageRepository>();
        var first = await links.ListVisibleAsync(tenant, ticket.Id, before: null, take: 2, Ct);
        var cursor = (first[^1].OccurredAt, first[^1].MessageId);
        var second = await links.ListVisibleAsync(tenant, ticket.Id, cursor, take: 2, Ct);
        var otherTenant = await links.ListVisibleAsync(Guid.CreateVersion7(), ticket.Id, before: null, take: 10, Ct);

        Assert.Equal([newest, pinned], first.Select(link => link.MessageId));
        Assert.Equal([SupportTicketMessageState.Linked, SupportTicketMessageState.Pinned], first.Select(link => link.State));
        Assert.Equal([oldest], second.Select(link => link.MessageId));
        Assert.Empty(otherTenant);
    }

    [Fact]
    public async Task TheSameUnclassifiedMessageCannotBeRecordedTwice()
    {
        var (factory, tenant) = await NewTenantAsync();
        var conversation = Guid.CreateVersion7();
        var message = Guid.CreateVersion7();
        await RecordUnclassifiedAsync(factory, tenant, conversation, message);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => RecordUnclassifiedAsync(factory, tenant, conversation, message));

        Assert.Equal(SupportErrorCodes.UnclassifiedAlreadyRecorded, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        var unclassified = read.ServiceProvider.GetRequiredService<ISupportUnclassifiedRepository>();
        Assert.Equal(1, await unclassified.CountPendingAsync(tenant, conversation, Ct));
        Assert.Equal([message], (await unclassified.ListPendingAsync(tenant, conversation, Ct)).Select(item => item.MessageId));
        Assert.NotNull(await unclassified.FindAsync(tenant, message, Ct));
        Assert.Null(await unclassified.FindAsync(Guid.CreateVersion7(), message, Ct));
    }

    [Fact]
    public async Task AReplyClientIdIsUniquePerTicket()
    {
        var (factory, tenant) = await NewTenantAsync();
        var first = await FromConversationAsync(factory, tenant, 1, Guid.CreateVersion7());
        var second = await FromConversationAsync(factory, tenant, 2, Guid.CreateVersion7());
        await SaveAsync(factory, first);
        await SaveAsync(factory, second);
        var clientId = Guid.CreateVersion7();
        var author = Guid.CreateVersion7();
        await AddReplyAsync(factory, Reply(tenant, first.Id, clientId, author));
        await AddReplyAsync(factory, Reply(tenant, second.Id, clientId, author));

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => AddReplyAsync(factory, Reply(tenant, first.Id, clientId, author)));

        Assert.Equal(SupportErrorCodes.ReplyDuplicateClientId, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        var replies = read.ServiceProvider.GetRequiredService<ISupportReplyRepository>();
        var found = await replies.FindByClientIdAsync(tenant, first.Id, clientId, Ct);
        Assert.NotNull(found);
        Assert.Equal(SupportReplyChannel.Thread, found.Channel);
        Assert.Equal(SupportReplyDelivery.Text, found.Delivery);
        Assert.Equal(SupportReplyStatus.Pending, found.Status);
        Assert.Equal("Ya revisamos tu caso.", found.Text);
        Assert.Equal(author, found.AuthorMemberId);
        Assert.Single(await replies.ListAsync(tenant, first.Id, Ct));
    }

    [Fact]
    public async Task OpenTicketsOfAConversationExcludeClosedOnes()
    {
        var (factory, tenant) = await NewTenantAsync();
        var conversation = Guid.CreateVersion7();
        var customer = await SeedCustomerAsync(factory, tenant, "Cliente por otro canal");
        var actor = Guid.CreateVersion7();
        var open = await FromConversationAsync(factory, tenant, 1, conversation);
        var closed = await FromConversationAsync(factory, tenant, 2, conversation);
        closed.ChangeStatus(SupportTicketStatus.Resolved, actor, Now.AddHours(1), Clock);
        closed.ChangeStatus(SupportTicketStatus.Closed, actor, Now.AddHours(2), Clock);
        var elsewhere = await FromConversationAsync(factory, tenant, 3, Guid.CreateVersion7(), customerId: customer);
        foreach (var ticket in new[] { open, closed, elsewhere })
        {
            await SaveAsync(factory, ticket);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        var openOnes = await tickets.ListOpenByConversationAsync(tenant, conversation, Ct);
        var related = await tickets.ListByConversationOrCustomerAsync(tenant, conversation, customer, Ct);
        var otherTenant = await tickets.ListOpenByConversationAsync(Guid.CreateVersion7(), conversation, Ct);

        Assert.Equal([open.Id], openOnes.Select(ticket => ticket.Id));
        Assert.Equal(
            new[] { open.Id, closed.Id, elsewhere.Id }.OrderBy(id => id.Value),
            related.Select(ticket => ticket.Id).OrderBy(id => id.Value));
        Assert.Empty(otherTenant);
        Assert.False((await tickets.FindAsync(tenant, closed.Id, Ct))!.FollowThread);
    }

    [Fact]
    public async Task TheEventKindCheckAcceptsTheNewKinds()
    {
        var (factory, tenant) = await NewTenantAsync();
        var ticket = await FromConversationAsync(factory, tenant, 1, Guid.CreateVersion7());
        var actor = Guid.CreateVersion7();
        ticket.RecordMessageEvent(SupportTicketEventKind.MessagePinned, Guid.CreateVersion7(), actor, Now.AddMinutes(1));
        ticket.SetFollowThread(false, actor, Now.AddMinutes(2));
        await SaveAsync(factory, ticket);

        await using var read = factory.Services.CreateAsyncScope();
        var events = await read.ServiceProvider.GetRequiredService<ISupportTicketRepository>().ListEventsAsync(tenant, ticket.Id, Ct);

        Assert.Equal(
            [SupportTicketEventKind.Created, SupportTicketEventKind.MessagePinned, SupportTicketEventKind.FollowThreadChanged],
            events.Select(item => item.Kind));
    }

    // Revisión del bloque A (AM2): el CHECK y el varchar(24) aceptan todos los tipos del enum, no sólo los que la prueba de arriba sabe
    // producir. Un tipo nuevo que no entre en la migración se pone rojo acá y no en producción.
    [Fact]
    public async Task TheEventKindCheckAcceptsEveryKind()
    {
        var (factory, tenant) = await NewTenantAsync();
        var ticket = await FromConversationAsync(factory, tenant, 1, Guid.CreateVersion7());
        await SaveAsync(factory, ticket);
        var kinds = Enum.GetValues<SupportTicketEventKind>();

        for (var index = 0; index < kinds.Length; index++)
        {
            await ExecuteAsync(
                factory,
                """
                INSERT INTO support.ticket_events (id, tenant_id, ticket_id, ordinal, kind, actor_member_id, occurred_at, details)
                VALUES (@id, @tenant, @ticket, @ordinal, @kind, NULL, now(), '{}'::jsonb)
                """,
                ("id", Guid.CreateVersion7()), ("tenant", tenant), ("ticket", ticket.Id.Value), ("ordinal", index + 100), ("kind", kinds[index].ToString()));
        }

        await using var read = factory.Services.CreateAsyncScope();
        var events = await read.ServiceProvider.GetRequiredService<ISupportTicketRepository>().ListEventsAsync(tenant, ticket.Id, Ct);
        Assert.Equal(kinds, events.Where(item => item.Ordinal >= 100).Select(item => item.Kind));
    }

    private async Task<(QepApiFactory Factory, Guid Tenant)> NewTenantAsync()
    {
        var factory = await sharedHost.StartTestAsync();
        var tenantId = NewTenantId();
        await SeedTenantAsync(factory, tenantId);
        return (factory, Guid.Parse(tenantId));
    }

    private static async Task<SupportTicket> FromConversationAsync(
        QepApiFactory factory, Guid tenant, long number, Guid conversation, string channel = "WhatsApp", Guid? customerId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenant, Ct);
        var type = (await scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().ListAsync(tenant, Ct))[0];
        return SupportTicket.CreateFromConversation(
            SupportTicketId.New(), tenant, number, type, $"Asunto {number}", "Lo escribió por el hilo.",
            SupportTicketPriority.Medium,
            new SupportConversationRef(conversation, channel), customerId, Person, null, Guid.CreateVersion7(), Now, Clock);
    }

    private static async Task SaveAsync(QepApiFactory factory, SupportTicket ticket)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().Add(ticket);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task LinkAsync(
        QepApiFactory factory, Guid tenant, SupportTicketId ticketId, Guid conversation, Guid message, DateTimeOffset occurredAt,
        SupportTicketMessageState state)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportTicketMessageRepository>()
            .Add(SupportTicketMessage.Link(tenant, ticketId, conversation, message, occurredAt, state, Now.AddMinutes(10)));
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task RecordUnclassifiedAsync(QepApiFactory factory, Guid tenant, Guid conversation, Guid message)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportUnclassifiedRepository>()
            .Add(SupportUnclassifiedMessage.Create(tenant, conversation, message, Now.AddMinutes(1), Now.AddMinutes(2)));
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static SupportTicketReply Reply(Guid tenant, SupportTicketId ticketId, Guid clientId, Guid author) =>
        SupportTicketReply.Start(
            tenant, ticketId, clientId, SupportReplyChannel.Thread, SupportReplyDelivery.Text, "Ya revisamos tu caso.", null, author,
            Now.AddMinutes(5));

    private static async Task AddReplyAsync(QepApiFactory factory, SupportTicketReply reply)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportReplyRepository>().Add(reply);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    private sealed class WeekdayCalendar : ISupportBusinessCalendar
    {
        public bool IsBusinessDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
