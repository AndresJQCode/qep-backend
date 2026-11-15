using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Soporte fase 2 (Tarea 9, plan E7 a E9): el expediente del ticket (fijar, soltar, la página de mensajes), «Seguir el hilo», «¿De qué
/// ticket es?» (asignar y «De ninguno») y <c>TicketClosed</c> en el hilo. Host de la clase con el seguimiento apagado: la prueba lo
/// corre a mano cuando lo necesita.
/// </summary>
public sealed class SupportTicketThreadApiTests(SupportHostWithoutWorkers host) : IClassFixture<SupportHostWithoutWorkers>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PinningAnOlderMessageAddsItToTheFile()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var older = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddDays(-2));
        var ticket = await scenario.CreateFromConversationAsync(conversation);

        var response = await scenario.PostActionAsync($"tickets/{Id(ticket)}/messages/{older}/pin", null, Version(ticket));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        var pinned = await JsonAsync(response);
        Assert.Equal(Version(ticket) + 1, Version(pinned));
        var last = pinned.GetProperty("events").EnumerateArray().Last();
        Assert.Equal("MessagePinned", last.GetProperty("kind").GetString());
        Assert.Equal(older, last.GetProperty("messageId").GetGuid());
        Assert.Equal(1, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.TicketMessagePinned, Id(ticket).ToString()));
        var page = await MessagesAsync(scenario, Id(ticket));
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("Pinned", item.GetProperty("linkState").GetString());
        Assert.Equal(older, item.GetProperty("message").GetProperty("id").GetGuid());
        Assert.Equal("Inbound", item.GetProperty("message").GetProperty("direction").GetString());
        Assert.Equal("Hola, tengo un problema con mi pedido", item.GetProperty("message").GetProperty("text").GetString());
    }

    // Revisión final (M6): fijar un entrante que esperaba en «sin clasificar» lo clasifica en ese ticket, en el mismo guardado: si no,
    // seguiría pidiéndole al asesor que decida de qué ticket es.
    [Fact]
    public async Task PinningAnUnclassifiedMessageAssignsItsPendingRowToTheTicket()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var chosen = await scenario.CreateFromConversationAsync(conversation);
        await scenario.CreateFromConversationAsync(conversation, "Otro caso");
        var inbound = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddMinutes(1));
        await FollowAsync();
        Assert.Single((await UnclassifiedAsync(scenario, conversation)).GetProperty("items").EnumerateArray());

        var current = await scenario.DetailAsync(Id(chosen));
        var response = await scenario.PostActionAsync($"tickets/{Id(chosen)}/messages/{inbound}/pin", null, Version(current));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        Assert.Empty((await UnclassifiedAsync(scenario, conversation)).GetProperty("items").EnumerateArray());
        Assert.Equal(
            "Assigned",
            await TextAsync(scenario.Factory, "SELECT resolution FROM support.unclassified_messages WHERE message_id = @id", ("id", inbound)));
        Assert.Equal(
            Id(chosen).ToString(),
            await TextAsync(
                scenario.Factory, "SELECT assigned_ticket_id::text FROM support.unclassified_messages WHERE message_id = @id", ("id", inbound)));
    }

    [Fact]
    public async Task PinningAMessageOfAnotherConversationIs422()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var other = await SeedConversationAsync(scenario.Factory, scenario.TenantId, waId: "573009998877");
        var foreign = await SeedMessageAsync(scenario.Factory, scenario.TenantId, other, DateTimeOffset.UtcNow.AddMinutes(-5));
        var ticket = await scenario.CreateFromConversationAsync(conversation);

        var response = await scenario.PostActionAsync($"tickets/{Id(ticket)}/messages/{foreign}/pin", null, Version(ticket));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.message.not_in_conversation", await CodeOfAsync(response));
        Assert.Equal(Version(ticket), Version(await scenario.DetailAsync(Id(ticket))));
    }

    [Fact]
    public async Task ReleasingKeepsTheWorkerFromLinkingItAgain()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var ticket = await scenario.CreateFromConversationAsync(conversation);
        var inbound = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddMinutes(1));
        await FollowAsync();
        Assert.Equal("Linked", (await MessagesAsync(scenario, Id(ticket))).GetProperty("items")[0].GetProperty("linkState").GetString());

        var response = await scenario.PostActionAsync($"tickets/{Id(ticket)}/messages/{inbound}/release", null, Version(ticket));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        Assert.Equal("MessageReleased", (await JsonAsync(response)).GetProperty("events").EnumerateArray().Last().GetProperty("kind").GetString());
        await FollowAsync();
        Assert.Empty((await MessagesAsync(scenario, Id(ticket))).GetProperty("items").EnumerateArray());
        Assert.Equal(
            "Released",
            await TextAsync(scenario.Factory, "SELECT state FROM support.ticket_messages WHERE message_id = @id", ("id", inbound)));

        var notLinked = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddDays(-1));
        var released = await scenario.DetailAsync(Id(ticket));
        var missing = await scenario.PostActionAsync($"tickets/{Id(ticket)}/messages/{notLinked}/release", null, Version(released));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("support.message.not_linked", await CodeOfAsync(missing));
    }

    [Fact]
    public async Task TurningFollowOffShowsTheBannerDate()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var ticket = await scenario.CreateFromConversationAsync(conversation);

        var response = await scenario.ActAsync(Id(ticket), "follow-thread", new { follow = false }, Version(ticket));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        var off = await JsonAsync(response);
        Assert.False(off.GetProperty("followsThread").GetBoolean());
        Assert.Equal(JsonValueKind.String, off.GetProperty("followOffAt").ValueKind);
        var last = off.GetProperty("events").EnumerateArray().Last();
        Assert.Equal("FollowThreadChanged", last.GetProperty("kind").GetString());
        Assert.False(last.GetProperty("follow").GetBoolean());
        Assert.Equal(1, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.TicketFollowChanged, Id(ticket).ToString()));

        // A-c2: un ticket cerrado no vuelve a seguir el hilo.
        var resolved = await scenario.MoveAsync(off, "Resolved");
        var closed = await scenario.MoveAsync(resolved, "Closed");
        var reopenFollow = await scenario.ActAsync(Id(ticket), "follow-thread", new { follow = true }, Version(closed));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reopenFollow.StatusCode);
        Assert.Equal("support.ticket.follow_closed", await CodeOfAsync(reopenFollow));
    }

    [Fact]
    public async Task AssigningAnUnclassifiedPinsItAndReopensAWaitingTicket()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var waiting = await scenario.MoveAsync(await scenario.CreateFromConversationAsync(conversation), "WaitingOnCustomer");
        var other = await scenario.CreateFromConversationAsync(conversation, "Otro caso");
        var inbound = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddMinutes(1));
        await FollowAsync();

        var before = await UnclassifiedAsync(scenario, conversation);
        var row = Assert.Single(before.GetProperty("items").EnumerateArray());
        Assert.Equal(inbound, row.GetProperty("message").GetProperty("id").GetGuid());
        Assert.Equal(
            new[] { Id(waiting), Id(other) }.Order(),
            row.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("id").GetGuid()).Order());

        var current = await scenario.DetailAsync(Id(waiting));
        var response = await scenario.PostActionAsync(
            $"conversations/{conversation}/unclassified/{inbound}/assign", new { ticketId = Id(waiting), version = Version(current) }, null);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        Assert.Empty((await JsonAsync(response)).GetProperty("items").EnumerateArray());
        var assigned = await scenario.DetailAsync(Id(waiting));
        Assert.Equal("Open", assigned.GetProperty("status").GetString());
        Assert.Contains(
            assigned.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "MessageClassified" && item.GetProperty("messageId").GetGuid() == inbound);
        var page = await MessagesAsync(scenario, Id(waiting));
        Assert.Equal("Pinned", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("linkState").GetString());
        Assert.Equal(1, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.UnclassifiedAssigned, inbound.ToString()));

        var again = await scenario.PostActionAsync(
            $"conversations/{conversation}/unclassified/{inbound}/assign", new { ticketId = Id(other), version = Version(other) }, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("support.unclassified.not_pending", await CodeOfAsync(again));
    }

    [Fact]
    public async Task DismissingWritesANoteInEveryCandidate()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var first = await scenario.CreateFromConversationAsync(conversation);
        var second = await scenario.CreateFromConversationAsync(conversation, "Otro caso");
        var inbound = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddMinutes(1));
        await FollowAsync();

        var response = await scenario.PostActionAsync($"conversations/{conversation}/unclassified/{inbound}/dismiss", null, null);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        Assert.Empty((await JsonAsync(response)).GetProperty("items").EnumerateArray());
        foreach (var ticket in new[] { first, second })
        {
            var detail = await scenario.DetailAsync(Id(ticket));
            Assert.Contains(
                detail.GetProperty("events").EnumerateArray(),
                item => item.GetProperty("kind").GetString() == "MessageDismissed" && item.GetProperty("messageId").GetGuid() == inbound);
            Assert.Empty((await MessagesAsync(scenario, Id(ticket))).GetProperty("items").EnumerateArray());
        }

        Assert.Equal(
            "Dismissed",
            await TextAsync(scenario.Factory, "SELECT resolution FROM support.unclassified_messages WHERE message_id = @id", ("id", inbound)));
    }

    [Fact]
    public async Task ClosingATicketWritesTicketClosedInTheThread()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var ticket = await scenario.CreateFromConversationAsync(conversation);

        var resolved = await scenario.MoveAsync(ticket, "Resolved");
        Assert.Equal(0, await CountTicketEventsAsync(scenario.Factory, conversation, Id(ticket), "TicketClosed"));
        var closed = await scenario.MoveAsync(resolved, "Closed");

        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.False(closed.GetProperty("followsThread").GetBoolean());
        Assert.Equal(1, await CountTicketEventsAsync(scenario.Factory, conversation, Id(ticket), "TicketClosed"));
    }

    [Fact]
    public async Task TicketMessagesPageNewestFirstWithoutReleased()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var ticket = await scenario.CreateFromConversationAsync(conversation);
        var now = DateTimeOffset.UtcNow;
        var oldest = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, now.AddMinutes(1));
        var middle = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, now.AddMinutes(2));
        var newest = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, now.AddMinutes(3));
        await FollowAsync();
        var release = await scenario.PostActionAsync($"tickets/{Id(ticket)}/messages/{middle}/release", null, Version(ticket));
        Assert.True(release.StatusCode == HttpStatusCode.OK, await BodyAsync(release));

        var first = await MessagesAsync(scenario, Id(ticket), take: 1);
        Assert.Equal([newest], Ids(first));
        var cursor = first.GetProperty("next").GetProperty("before").GetString();
        var second = await MessagesAsync(scenario, Id(ticket), take: 1, before: cursor);
        Assert.Equal([oldest], Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next").ValueKind);

        var all = await MessagesAsync(scenario, Id(ticket));
        Assert.Equal([newest, oldest], Ids(all));

        // Un cursor que no salió de una página anterior es 422 validation.failed en Before, no una página vacía.
        var garbage = await scenario.Client.GetAsync($"{scenario.Url}/tickets/{Id(ticket)}/messages?before=no-es-un-cursor", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, garbage.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(garbage));
        Assert.Equal(["Before"], await ErrorKeysAsync(garbage));
    }

    [Fact]
    public async Task AStaleVersionIs412()
    {
        var scenario = await StartAsync(host.Factory);
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var message = await SeedMessageAsync(scenario.Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddDays(-1));
        var ticket = await scenario.CreateFromConversationAsync(conversation);
        var moved = await scenario.MoveAsync(ticket, "Open");

        var stale = await scenario.PostActionAsync($"tickets/{Id(ticket)}/messages/{message}/pin", null, Version(ticket));
        var withoutVersion = await scenario.PostActionAsync($"tickets/{Id(ticket)}/messages/{message}/pin", null, null);
        var staleFollow = await scenario.ActAsync(Id(ticket), "follow-thread", new { follow = false }, Version(ticket));

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutVersion.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleFollow.StatusCode);
        Assert.Equal(Version(moved), Version(await scenario.DetailAsync(Id(ticket))));
    }

    private async Task FollowAsync()
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SupportThreadFollower>().ProcessBatchAsync(Ct);
    }

    private static async Task<JsonElement> MessagesAsync(SupportTicketScenario scenario, Guid ticketId, int? take = null, string? before = null)
    {
        var query = new List<string>();
        if (take is { } size)
        {
            query.Add($"take={size}");
        }

        if (before is not null)
        {
            query.Add($"before={Uri.EscapeDataString(before)}");
        }

        var url = $"{scenario.Url}/tickets/{ticketId}/messages" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
        var response = await scenario.Client.GetAsync(url, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> UnclassifiedAsync(SupportTicketScenario scenario, Guid conversation)
    {
        var response = await scenario.Client.GetAsync($"{scenario.Url}/conversations/{conversation}/unclassified", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonAsync(response);
    }

    private static Guid[] Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("message").GetProperty("id").GetGuid()).ToArray();
}
