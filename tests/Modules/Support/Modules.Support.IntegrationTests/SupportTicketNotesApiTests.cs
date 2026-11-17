using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-15 §5.4 and plan Task 4: internal notes with text. A note never bumps the ticket's version nor writes its trace, is
/// idempotent by <c>clientId</c> (plan N15) and pages newest first. One host for the class; each test seeds its own tenant.
/// </summary>
public sealed class SupportTicketNotesApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AManagerAddsANoteAndReadsItBack()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        var response = await PostNoteAsync(scenario, Id(ticket), Guid.CreateVersion7(), "  El cliente pidió que lo llamen en la tarde.  ");

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var note = await JsonAsync(response);
        Assert.Equal("El cliente pidió que lo llamen en la tarde.", note.GetProperty("text").GetString());
        Assert.Equal(scenario.CallerMemberId, note.GetProperty("author").GetProperty("id").GetGuid());
        Assert.Equal("Zoe Llamante", note.GetProperty("author").GetProperty("displayName").GetString());
        Assert.Empty(note.GetProperty("files").EnumerateArray());
        var page = await NotesAsync(scenario, Id(ticket));
        var first = page.GetProperty("items").EnumerateArray().First();
        Assert.Equal(note.GetProperty("id").GetGuid(), first.GetProperty("id").GetGuid());
        Assert.Equal(note.GetProperty("createdAt").GetDateTimeOffset(), first.GetProperty("createdAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task TheSameClientIdReturnsTheSameNote()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        var clientId = Guid.CreateVersion7();

        var first = await PostNoteAsync(scenario, Id(ticket), clientId, "Llamar el lunes.");
        var again = await PostNoteAsync(scenario, Id(ticket), clientId, "Llamar el lunes.");

        Assert.True(first.StatusCode == HttpStatusCode.Created, await BodyAsync(first));
        Assert.True(again.StatusCode == HttpStatusCode.Created, await BodyAsync(again));
        var a = await JsonAsync(first);
        var b = await JsonAsync(again);
        Assert.Equal(clientId, a.GetProperty("id").GetGuid());
        Assert.Equal(a.GetProperty("id").GetGuid(), b.GetProperty("id").GetGuid());
        Assert.Equal(a.GetProperty("createdAt").GetDateTimeOffset(), b.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(1L, await ScalarAsync(
            scenario.Factory, "SELECT count(*) FROM support.ticket_notes WHERE tenant_id = @tenant", ("tenant", scenario.TenantId)));
    }

    // Plan N15: the clientId is the note's id, so the same one on another ticket (or by another author) is a conflict, not a replay.
    [Fact]
    public async Task TheSameClientIdOnAnotherTicketIs409()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        var other = await scenario.CreateAsync();
        var clientId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await PostNoteAsync(scenario, Id(ticket), clientId, "Primera nota.")).StatusCode);

        var response = await PostNoteAsync(scenario, Id(other), clientId, "Primera nota.");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AClosedTicketRejectsANewNoteButReplaysAnOldOne()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        var clientId = Guid.CreateVersion7();
        var before = await PostNoteAsync(scenario, Id(ticket), clientId, "Antes de cerrar.");
        Assert.True(before.StatusCode == HttpStatusCode.Created, await BodyAsync(before));
        await scenario.MoveAsync(await scenario.MoveAsync(await scenario.MoveAsync(ticket, "Open"), "Resolved"), "Closed");

        var fresh = await PostNoteAsync(scenario, Id(ticket), Guid.CreateVersion7(), "Después de cerrar.");
        var replay = await PostNoteAsync(scenario, Id(ticket), clientId, "Antes de cerrar.");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, fresh.StatusCode);
        Assert.Equal(SupportErrorCodes.NoteTicketClosed, await CodeOfAsync(fresh));
        Assert.True(replay.StatusCode == HttpStatusCode.Created, await BodyAsync(replay));
        Assert.Equal(clientId, (await JsonAsync(replay)).GetProperty("id").GetGuid());
    }

    // Review Focus 5: the note is not part of the aggregate. The If-Match loaded before it still works, and the trace gains nothing.
    [Fact]
    public async Task ANoteDoesNotBumpTheVersionNorWriteTheTrace()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        var events = ticket.GetProperty("events").GetArrayLength();
        var traceRows = await TraceRowsAsync(scenario, Id(ticket));

        var note = await PostNoteAsync(scenario, Id(ticket), Guid.CreateVersion7(), "Nota interna.");

        Assert.True(note.StatusCode == HttpStatusCode.Created, await BodyAsync(note));
        var detail = await scenario.DetailAsync(Id(ticket));
        Assert.Equal(Version(ticket), Version(detail));
        Assert.Equal(events, detail.GetProperty("events").GetArrayLength());
        Assert.Equal(traceRows, await TraceRowsAsync(scenario, Id(ticket)));
        var moved = await scenario.ActAsync(Id(ticket), "status", new ChangeTicketStatusRequest("Open"), Version(ticket));
        Assert.True(moved.StatusCode == HttpStatusCode.OK, await BodyAsync(moved));
    }

    [Fact]
    public async Task NotesPageNewestFirstWithACursor()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        var ids = new List<Guid>();
        foreach (var text in new[] { "Primera.", "Segunda.", "Tercera." })
        {
            var response = await PostNoteAsync(scenario, Id(ticket), Guid.CreateVersion7(), text);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
            ids.Add((await JsonAsync(response)).GetProperty("id").GetGuid());
        }

        var firstPage = await NotesAsync(scenario, Id(ticket), "take=2");
        var cursor = firstPage.GetProperty("next").GetProperty("before").GetString();
        var secondPage = await NotesAsync(scenario, Id(ticket), $"take=2&before={cursor}");

        Assert.Equal([ids[2], ids[1]], firstPage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal([ids[0]], secondPage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("next").ValueKind);
        foreach (var query in new[] { "take=0", "take=101", "before=no-es-un-cursor" })
        {
            var invalid = await scenario.Client.GetAsync($"{scenario.Url}/tickets/{Id(ticket)}/notes?{query}", Ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            Assert.Equal("validation.failed", await CodeOfAsync(invalid));
        }
    }

    [Fact]
    public async Task AnInvalidBodyIs422WithItsField()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        var noClient = await scenario.Client.PostAsJsonAsync(
            $"{scenario.Url}/tickets/{Id(ticket)}/notes", new { text = "Sin clientId." }, Ct);
        var badText = await PostNoteAsync(scenario, Id(ticket), Guid.CreateVersion7(), "hola\u0007");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noClient.StatusCode);
        Assert.Equal(["ClientId"], await ErrorKeysAsync(noClient));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badText.StatusCode);
        Assert.Equal(["Text"], await ErrorKeysAsync(badText));
    }

    [Fact]
    public async Task AReaderListsButCannotAdd()
    {
        var factory = await sharedHost.StartTestAsync();
        var scenario = await StartAsync(factory);
        var ticket = await scenario.CreateAsync();
        Assert.Equal(HttpStatusCode.Created, (await PostNoteAsync(scenario, Id(ticket), Guid.CreateVersion7(), "Visible.")).StatusCode);
        using var reader = CreateClient(factory, scenario.SubjectId, scenario.Tenant, SupportPermissions.Read);
        var foreign = await StartAsync(factory);
        var foreignTicket = await foreign.CreateAsync();

        var read = await reader.GetAsync($"{scenario.Url}/tickets/{Id(ticket)}/notes", Ct);
        var add = await reader.PostAsJsonAsync(
            $"{scenario.Url}/tickets/{Id(ticket)}/notes", new { clientId = Guid.CreateVersion7(), text = "No puedo." }, Ct);
        var otherTenantRoute = await scenario.Client.GetAsync($"{foreign.Url}/tickets/{Id(foreignTicket)}/notes", Ct);
        var foreignTicketInOwnRoute = await scenario.Client.GetAsync($"{scenario.Url}/tickets/{Id(foreignTicket)}/notes", Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Single((await JsonAsync(read)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherTenantRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignTicketInOwnRoute.StatusCode);
        Assert.Equal(SupportErrorCodes.TicketNotFound, await CodeOfAsync(foreignTicketInOwnRoute));
    }

    [Fact]
    public async Task AnAuditEntryIsWritten()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        var response = await PostNoteAsync(scenario, Id(ticket), Guid.CreateVersion7(), "Auditada.");

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        Assert.Equal(1, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.TicketNoteAdded, Id(ticket).ToString()));
    }

    internal static Task<HttpResponseMessage> PostNoteAsync(
        SupportTicketScenario scenario, Guid ticketId, Guid clientId, string text, IReadOnlyList<Guid>? fileIds = null) =>
        scenario.Client.PostAsJsonAsync($"{scenario.Url}/tickets/{ticketId}/notes", new { clientId, text, fileIds }, Ct);

    private static Task<long> TraceRowsAsync(SupportTicketScenario scenario, Guid ticketId) =>
        ScalarAsync(scenario.Factory, "SELECT count(*) FROM support.ticket_events WHERE ticket_id = @ticket", ("ticket", ticketId));

    private static async Task<JsonElement> NotesAsync(SupportTicketScenario scenario, Guid ticketId, string? query = null)
    {
        var response = await scenario.Client.GetAsync(
            $"{scenario.Url}/tickets/{ticketId}/notes{(query is null ? string.Empty : "?" + query)}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonAsync(response);
    }
}
