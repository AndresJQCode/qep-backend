using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Fase 4b, Tarea 14 (H22, owner 2026-11-16, Q-4, Review Focus 4): el cierre automático contra la base real. Host propio por prueba, sin
/// workers, con el reloj del tenant movible: la prueba lo adelanta y corre un tick del worker a mano. El ticket nace de una conversación
/// sembrada; los entrantes se vinculan a mano en <c>support.ticket_messages</c>, como lo haría el seguimiento del hilo.
/// </summary>
public sealed class SupportAutoCloseApiTests : IAsyncLifetime
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    private readonly MutableTenantClock _clock = new(ClockedSupportHost.Reception);
    private readonly RecordingSupportRealtime _realtime = new();
    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    private QepApiFactory Factory => _factory ?? throw new InvalidOperationException("The host is not started.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        (_database, _factory) = await OpenOwnHostAsync(services =>
        {
            services.RemoveAll<ITenantClock>();
            services.AddSingleton<ITenantClock>(_clock);
            services.RemoveAll<ISupportRealtime>();
            services.AddSingleton<ISupportRealtime>(_realtime);
        });

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

    // H22: el día del N-ésimo día hábil sigue Resuelto; al día siguiente lo cierra el sistema: traza sin actor, auditoría del sistema y
    // TicketClosed en el hilo.
    [Fact]
    public async Task AResolvedTicketClosesAfterNBusinessDays()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);

        await TickAtAsync(closeOn);
        Assert.Equal("Resolved", (await rig.Scenario.DetailAsync(rig.TicketId)).GetProperty("status").GetString());

        await TickAtAsync(closeOn.AddDays(1));

        var detail = await rig.Scenario.DetailAsync(rig.TicketId);
        Assert.Equal("Closed", detail.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("clock").GetProperty("autoCloseOn").ValueKind);
        var last = detail.GetProperty("events").EnumerateArray().Last();
        Assert.Equal("StatusChanged", last.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("actor").ValueKind);
        Assert.Equal(
            1L,
            await ScalarAsync(
                Factory,
                "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_id = @id AND actor_id = @system",
                ("action", SupportAuditActions.TicketStatusChanged),
                ("id", rig.TicketId.ToString()),
                ("system", Guid.Empty)));
        Assert.Equal(1L, await CountTicketEventsAsync(Factory, rig.Conversation, rig.TicketId, "TicketClosed"));
    }

    // Review Focus 4: una respuesta del cliente después de resolver, vinculada al ticket, corre la fecha.
    [Fact]
    public async Task ACustomerReplyAfterResolvingRestartsTheCount()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);
        await LinkInboundAsync(rig, ClockedSupportHost.Reception.AddDays(4), "Linked");

        var moved = await AutoCloseOnAsync(rig);
        await TickAtAsync(closeOn.AddDays(1));

        Assert.True(moved > closeOn);
        Assert.Equal(await ExpectedAsync(rig, new DateOnly(2026, 11, 17)), moved);
        Assert.Equal("Resolved", (await rig.Scenario.DetailAsync(rig.TicketId)).GetProperty("status").GetString());
    }

    // Q-4: soltar la respuesta la saca del expediente y la fecha vuelve a la de la resolución.
    [Fact]
    public async Task ReleasingTheReplyMovesTheCloseDateBack()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);
        var link = await LinkInboundAsync(rig, ClockedSupportHost.Reception.AddDays(4), "Linked");
        Assert.True(await AutoCloseOnAsync(rig) > closeOn);

        await ExecuteAsync(Factory, "UPDATE support.ticket_messages SET state = 'Released' WHERE id = @id", ("id", link));

        Assert.Equal(closeOn, await AutoCloseOnAsync(rig));
    }

    // Owner, 2026-11-16: fijada también sigue en el expediente.
    [Fact]
    public async Task APinnedReplyAlsoHoldsTheClose()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);

        await LinkInboundAsync(rig, ClockedSupportHost.Reception.AddDays(4), "Pinned");

        Assert.True(await AutoCloseOnAsync(rig) > closeOn);
    }

    // Q-4: el toque de la encuesta no es una respuesta del cliente, aunque el seguimiento lo haya vinculado.
    [Fact]
    public async Task ASurveyAnswerDoesNotHoldTheClose()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);
        var surveyMessage = await SeedMessageAsync(
            Factory, rig.TenantId, rig.Conversation, ClockedSupportHost.Reception.AddHours(1), direction: Outbound, text: "Encuesta");
        await ExecuteAsync(
            Factory,
            "UPDATE support.ticket_surveys SET status = 'Sent', channel = 'WhatsApp', message_id = @message, sent_at = @at WHERE ticket_id = @id",
            ("message", surveyMessage), ("at", ClockedSupportHost.Reception.AddHours(1)), ("id", rig.TicketId));

        await LinkInboundAsync(
            rig, ClockedSupportHost.Reception.AddDays(4), "Linked", text: "Buena", kind: InteractiveKind, replyTo: surveyMessage);

        Assert.Equal(closeOn, await AutoCloseOnAsync(rig));
    }

    // Q-4: el comentario de la encuesta tampoco.
    [Fact]
    public async Task TheSurveyCommentDoesNotHoldTheClose()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);
        var comment = Guid.CreateVersion7();
        await LinkInboundAsync(rig, ClockedSupportHost.Reception.AddDays(4), "Linked", text: "Muy amables.", messageId: comment);

        await ExecuteAsync(
            Factory,
            """
            UPDATE support.ticket_surveys
            SET status = 'Sent', channel = 'WhatsApp', sent_at = @at, rating = 'Good', answered_via = 'WhatsApp', answered_at = @at,
                comment = 'Muy amables.', comment_message_id = @comment
            WHERE ticket_id = @id
            """,
            ("at", ClockedSupportHost.Reception.AddHours(1)), ("comment", comment), ("id", rig.TicketId));

        Assert.Equal(closeOn, await AutoCloseOnAsync(rig));
    }

    // Revisión del bloque D: el seguimiento del hilo vincula cada entrante a todos los tickets abiertos de la conversación, así que el
    // toque y el comentario de la encuesta del ticket A quedan vinculados también al ticket B. Tampoco cuentan para B.
    [Fact]
    public async Task ASiblingTicketsSurveyAnswerDoesNotHoldTheClose()
    {
        var sibling = await ResolvedTicketAsync();
        var other = await SupportTicketSeed.SaveConversationTicketAsync(
            Factory, sibling.TenantId, sibling.Conversation, ClockedSupportHost.Reception);
        await sibling.Scenario.MoveAsync(await sibling.Scenario.DetailAsync(other.Id.Value), "Resolved");
        var rig = sibling with { TicketId = other.Id.Value };
        var closeOn = await AutoCloseOnAsync(rig);
        var surveyMessage = await SeedMessageAsync(
            Factory, sibling.TenantId, sibling.Conversation, ClockedSupportHost.Reception.AddHours(1), direction: Outbound, text: "Encuesta");
        var comment = Guid.CreateVersion7();
        await ExecuteAsync(
            Factory,
            """
            UPDATE support.ticket_surveys
            SET status = 'Sent', channel = 'WhatsApp', message_id = @message, sent_at = @at, rating = 'Good', answered_via = 'WhatsApp',
                answered_at = @at, comment = 'Muy amables.', comment_message_id = @comment
            WHERE ticket_id = @id
            """,
            ("message", surveyMessage), ("at", ClockedSupportHost.Reception.AddHours(1)), ("comment", comment), ("id", sibling.TicketId));

        await LinkInboundAsync(
            rig, ClockedSupportHost.Reception.AddDays(4), "Linked", text: "Buena", kind: InteractiveKind, replyTo: surveyMessage);
        await LinkInboundAsync(rig, ClockedSupportHost.Reception.AddDays(4).AddMinutes(1), "Linked", text: "Muy amables.", messageId: comment);

        Assert.Equal(closeOn, await AutoCloseOnAsync(rig));
    }

    // H21 (Q-10): la encuesta se sigue respondiendo después del cierre automático.
    [Fact]
    public async Task AClosedTicketCanStillBeRated()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);
        await ExecuteAsync(
            Factory,
            "UPDATE support.ticket_surveys SET status = 'Sent', channel = 'Portal', sent_at = @at WHERE ticket_id = @id",
            ("at", ClockedSupportHost.Reception.AddHours(1)), ("id", rig.TicketId));
        await TickAtAsync(closeOn.AddDays(1));
        var surveyId = Guid.Parse(
            (await TextAsync(Factory, "SELECT id::text FROM support.ticket_surveys WHERE ticket_id = @id", ("id", rig.TicketId)))!,
            System.Globalization.CultureInfo.InvariantCulture);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SupportSurveyAnswering>()
                .AnswerAsync(rig.TenantId, surveyId, SupportSurveyRating.Good, null, SupportSurveyChannel.Portal, Ct);
        }

        Assert.Equal(
            "Closed|Good",
            await TextAsync(
                Factory,
                "SELECT t.status || '|' || s.rating FROM support.tickets t JOIN support.ticket_surveys s ON s.ticket_id = t.id WHERE t.id = @id",
                ("id", rig.TicketId)));
    }

    // Review Focus 4: un ticket reabierto no lo cierra el sistema, aunque la fecha de la resolución haya pasado.
    [Fact]
    public async Task AReopenedTicketIsNotClosed()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);
        await rig.Scenario.MoveAsync(await rig.Scenario.DetailAsync(rig.TicketId), "Open");

        await TickAtAsync(closeOn.AddDays(10));

        Assert.Equal("Open", (await rig.Scenario.DetailAsync(rig.TicketId)).GetProperty("status").GetString());
    }

    // H22: el detalle muestra la fecha mientras está Resuelto, recalculada en cada lectura; abierto o cerrado, null.
    [Fact]
    public async Task TheDetailShowsAutoCloseOn()
    {
        var scenario = await StartAsync(Factory);
        var conversation = await SeedConversationAsync(Factory, scenario.TenantId);
        var ticket = await SupportTicketSeed.SaveConversationTicketAsync(Factory, scenario.TenantId, conversation, ClockedSupportHost.Reception);
        var open = await scenario.DetailAsync(ticket.Id.Value);

        var resolved = await scenario.MoveAsync(open, "Resolved");

        Assert.Equal(JsonValueKind.Null, open.GetProperty("clock").GetProperty("autoCloseOn").ValueKind);
        Assert.Equal(
            await ExpectedAsync(scenario.TenantId, DateOnly.FromDateTime(ClockedSupportHost.Reception.UtcDateTime)),
            DateOnly.Parse(resolved.GetProperty("clock").GetProperty("autoCloseOn").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    // Después del commit, el aviso del ticket por el hub y el del hilo.
    [Fact]
    public async Task ClosingAnnouncesTheTicket()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);
        _realtime.Clear();

        await TickAtAsync(closeOn.AddDays(1));

        Assert.Contains(rig.TicketId, _realtime.Tickets);
    }

    // Revisión del bloque D: el candidato se leyó en otra resolución (se reabrió y se volvió a resolver entre la lista y el cierre): el
    // cierre no lo toca.
    [Fact]
    public async Task ACandidateOfAnotherResolutionIsNotClosed()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);
        _clock.UtcNow = new DateTimeOffset(closeOn.AddDays(1).ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(-5));
        var candidate = Assert.Single(await CandidatesAsync());

        bool closed;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            closed = await scope.ServiceProvider.GetRequiredService<SupportAutoCloser>()
                .CloseIfDueAsync(candidate with { ResolvedAt = candidate.ResolvedAt.AddSeconds(-1) }, Ct);
        }

        Assert.False(closed);
        Assert.Equal("Resolved", (await rig.Scenario.DetailAsync(rig.TicketId)).GetProperty("status").GetString());
    }

    // Revisión del bloque D: el recorrido va por páginas de 100 (cursor por resolved_at e id): con más de una página, los cierra todos.
    [Fact]
    public async Task TheScanPagesThroughMoreThanOnePage()
    {
        _clock.UtcNow = ClockedSupportHost.Reception;
        var scenario = await StartAsync(Factory);
        var conversation = await SeedConversationAsync(Factory, scenario.TenantId);
        const int tickets = SupportAutoCloser.DefaultBatchSize + 5;
        for (var index = 0; index < tickets; index++)
        {
            await SupportTicketSeed.SaveConversationTicketAsync(Factory, scenario.TenantId, conversation, ClockedSupportHost.Reception);
        }

        await ExecuteAsync(
            Factory,
            """
            UPDATE support.tickets SET status = 'Resolved', resolved_at = @at, resolution_count = 1
            WHERE tenant_id = @tenant
            """,
            ("at", ClockedSupportHost.Reception), ("tenant", scenario.TenantId));

        _clock.UtcNow = ClockedSupportHost.Reception.AddDays(30);
        var closed = await ActivatorUtilities.CreateInstance<Modules.Support.Infrastructure.SupportAutoCloseWorker>(Factory.Services)
            .RunOnceAsync(Ct);

        Assert.Equal(tickets, closed);
        Assert.Equal(
            0L,
            await ScalarAsync(
                Factory, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant AND status <> 'Closed'", ("tenant", scenario.TenantId)));
    }

    // Revisión del bloque D: dos cierres a la vez del mismo ticket (dos réplicas en el mismo tick) dejan un solo TicketClosed en el
    // hilo y una sola auditoría: el segundo pierde por la versión del ticket y su transacción se deshace entera.
    [Fact]
    public async Task TwoConcurrentClosesLeaveOneClosedEventAndOneAudit()
    {
        var rig = await ResolvedTicketAsync();
        var closeOn = await AutoCloseOnAsync(rig);
        _clock.UtcNow = new DateTimeOffset(closeOn.AddDays(1).ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(-5));
        var candidate = Assert.Single(await CandidatesAsync());

        var outcomes = await Task.WhenAll(CloseAsync(candidate), CloseAsync(candidate));

        Assert.Single(outcomes, closed => closed);
        Assert.Equal("Closed", (await rig.Scenario.DetailAsync(rig.TicketId)).GetProperty("status").GetString());
        Assert.Equal(1L, await CountTicketEventsAsync(Factory, rig.Conversation, rig.TicketId, "TicketClosed"));
        Assert.Equal(
            1L,
            await ScalarAsync(
                Factory,
                "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_id = @id AND actor_id = @system",
                ("action", SupportAuditActions.TicketStatusChanged), ("id", rig.TicketId.ToString()), ("system", Guid.Empty)));
    }

    private async Task<IReadOnlyList<SupportAutoCloseCandidate>> CandidatesAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SupportAutoCloser>().ListCandidatesAsync(100, null, Ct);
    }

    // Como el worker: una RequestConcurrencyException es «otro le ganó».
    private async Task<bool> CloseAsync(SupportAutoCloseCandidate candidate)
    {
        try
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SupportAutoCloser>().CloseIfDueAsync(candidate, Ct);
        }
        catch (BuildingBlocks.Application.RequestConcurrencyException)
        {
            return false;
        }
    }

    // Un ticket de una conversación, resuelto por la API con el reloj en la radicación (viernes 13 de noviembre de 2026).
    private async Task<Rig> ResolvedTicketAsync()
    {
        _clock.UtcNow = ClockedSupportHost.Reception;
        var scenario = await StartAsync(Factory);
        var conversation = await SeedConversationAsync(Factory, scenario.TenantId);
        var ticket = await SupportTicketSeed.SaveConversationTicketAsync(Factory, scenario.TenantId, conversation, ClockedSupportHost.Reception);
        await scenario.MoveAsync(await scenario.DetailAsync(ticket.Id.Value), "Resolved");
        return new Rig(scenario, scenario.TenantId, ticket.Id.Value, conversation);
    }

    private static async Task<DateOnly> AutoCloseOnAsync(Rig rig)
    {
        var value = (await rig.Scenario.DetailAsync(rig.TicketId)).GetProperty("clock").GetProperty("autoCloseOn").GetString();
        return DateOnly.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
    }

    // La fecha que da el calendario real del tenant: el N-ésimo día hábil después de from.
    private Task<DateOnly> ExpectedAsync(Rig rig, DateOnly from) => ExpectedAsync(rig.TenantId, from);

    private async Task<DateOnly> ExpectedAsync(Guid tenantId, DateOnly from)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var clock = await scope.ServiceProvider.GetRequiredService<ISupportBusinessCalendarSource>().GetAsync(tenantId, Bogota, Ct);
        return SupportAutoCloser.AutoCloseOn(clock.Calendar, from, null, SupportTenantSettings.DefaultAutoCloseBusinessDays);
    }

    // Un entrante del cliente vinculado al ticket en el estado pedido. Devuelve el id del vínculo.
    private async Task<Guid> LinkInboundAsync(
        Rig rig,
        DateTimeOffset occurredAt,
        string state,
        string text = "Sigue fallando.",
        short kind = TextKind,
        Guid? replyTo = null,
        Guid? messageId = null)
    {
        var message = await SeedMessageAsync(
            Factory, rig.TenantId, rig.Conversation, occurredAt, text: text, kind: kind, replyTo: replyTo, messageId: messageId);
        var link = Guid.CreateVersion7();
        await ExecuteAsync(
            Factory,
            """
            INSERT INTO support.ticket_messages (id, tenant_id, ticket_id, conversation_id, message_id, state, occurred_at, created_at, updated_at)
            VALUES (@id, @tenant, @ticket, @conversation, @message, @state, @occurred, now(), now())
            """,
            ("id", link), ("tenant", rig.TenantId), ("ticket", rig.TicketId), ("conversation", rig.Conversation), ("message", message),
            ("state", state), ("occurred", occurredAt));
        return link;
    }

    // Un tick del cierre automático con el reloj del tenant a mediodía de ese día en Bogotá.
    private async Task TickAtAsync(DateOnly day)
    {
        _clock.UtcNow = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(-5));
        await ActivatorUtilities.CreateInstance<Modules.Support.Infrastructure.SupportAutoCloseWorker>(Factory.Services).RunOnceAsync(Ct);
    }

    private sealed record Rig(SupportTicketScenario Scenario, Guid TenantId, Guid TicketId, Guid Conversation);

    /// <summary>El aviso del hub que anota el ticket en vez de publicarlo.</summary>
    private sealed class RecordingSupportRealtime : ISupportRealtime
    {
        private readonly ConcurrentQueue<Guid> _tickets = new();

        public IReadOnlyList<Guid> Tickets => [.. _tickets];

        public void Clear() => _tickets.Clear();

        public Task TicketChangedAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken)
        {
            _tickets.Enqueue(ticketId);
            return Task.CompletedTask;
        }

        public Task TaskChangedAsync(Guid tenantId, Guid ticketId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            TicketChangedAsync(tenantId, ticketId, cancellationToken);
    }
}
