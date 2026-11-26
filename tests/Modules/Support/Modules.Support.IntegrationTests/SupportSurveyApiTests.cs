using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Fase 4b, Tarea 11 (H16, H17): la cola de las encuestas contra la base real. Host propio por prueba, sin los workers de Soporte: la
/// prueba reclama y procesa con <see cref="SupportSurveySender"/> a mano. El correo y el aviso del hub son dobles que anotan; la decisión
/// de canal la amarran <c>SupportSurveySenderTests</c>.
/// </summary>
public sealed class SupportSurveyApiTests : IAsyncLifetime
{
    private readonly RecordingSupportSurveyEmail _email = new();
    private readonly RecordingSupportRealtime _realtime = new();
    private readonly FakeSupportReplyChannels _channels = new();
    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    private QepApiFactory Factory => _factory ?? throw new InvalidOperationException("The host is not started.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        (_database, _factory) = await OpenOwnHostAsync(services =>
        {
            services.RemoveAll<ISupportSurveyEmail>();
            services.AddSingleton<ISupportSurveyEmail>(_email);
            services.RemoveAll<ISupportRealtime>();
            services.AddSingleton<ISupportRealtime>(_realtime);
            // Fase 4b, Tarea 12: el canal de WhatsApp es un doble que anota; lo que hace Mensajería con el envío lo amarran
            // SupportReplyOutboundTests.
            services.RemoveAll<ISupportReplyChannels>();
            services.AddSingleton<ISupportReplyChannels>(_channels);
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

    // FOR UPDATE SKIP LOCKED con locked_until: dos workers a la vez reparten las encuestas sin repetir ninguna, y una ya reclamada no se
    // vuelve a reclamar mientras dure su reclamo.
    [Fact]
    public async Task TwoWorkersNeverClaimTheSameRow()
    {
        var scenario = await StartAsync(Factory);
        var surveys = new List<Guid>();
        for (var index = 0; index < 6; index++)
        {
            await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");
        }

        surveys.AddRange(await SurveyIdsAsync(scenario));
        var claims = await Task.WhenAll(ClaimAsync(4), ClaimAsync(4));

        var claimed = claims.SelectMany(batch => batch.SurveyIds).ToList();
        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.Equal(surveys.Order(), claimed.Order());
        Assert.Empty((await ClaimAsync(10)).SurveyIds);
        Assert.Equal(
            6L,
            await ScalarAsync(
                Factory,
                "SELECT count(*) FROM support.ticket_surveys WHERE tenant_id = @tenant AND locked_until > now()",
                ("tenant", scenario.TenantId)));
    }

    // Cada resultado se guarda con su auditoría de sistema y, después del commit, avisa el ticket por el hub. Sin subir la versión.
    [Fact]
    public async Task ProcessingAnnouncesTheTicket()
    {
        var scenario = await StartAsync(Factory);
        await EnableSurveyAsync(scenario);
        var resolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");

        await ProcessClaimedAsync();

        var email = Assert.Single(_email.Sent);
        Assert.Equal("ana@example.com", email.ToAddress);
        Assert.Equal(
            "Sent|Email|true",
            await TextAsync(
                Factory,
                "SELECT status || '|' || channel || '|' || (token_hash IS NOT NULL)::text FROM support.ticket_surveys WHERE ticket_id = @id",
                ("id", Id(resolved))));
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveySent, Id(resolved).ToString()));
        Assert.Contains(Id(resolved), _realtime.Tickets);
        Assert.Equal(Version(resolved), Version(await scenario.DetailAsync(Id(resolved))));
        var survey = Assert.Single((await scenario.DetailAsync(Id(resolved))).GetProperty("surveys").EnumerateArray());
        Assert.Equal(("Sent", "Email"), (survey.GetProperty("status").GetString(), survey.GetProperty("channel").GetString()));
    }

    // El tenant con la encuesta apagada (el default): «sin encuesta» con survey_disabled, auditada como tal.
    [Fact]
    public async Task ASurveyOffTenantIsSkippedAndAudited()
    {
        var scenario = await StartAsync(Factory);
        var resolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");

        await ProcessClaimedAsync();

        Assert.Empty(_email.Sent);
        Assert.Equal(
            "Skipped|survey_disabled",
            await TextAsync(
                Factory, "SELECT status || '|' || reason_code FROM support.ticket_surveys WHERE ticket_id = @id", ("id", Id(resolved))));
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveySkipped, Id(resolved).ToString()));
    }

    // Revisión del bloque C: el reclamo del lote cubre un envío, no el lote entero. Si el lote tardó más que su reclamo y otro worker
    // volvió a reclamar la encuesta, el primero ya no la manda: renovar el reclamo exige que siga siendo el suyo.
    [Fact]
    public async Task AStaleBatchClaimDoesNotProcessTheSurveyTwice()
    {
        var scenario = await StartAsync(Factory);
        await EnableSurveyAsync(scenario);
        var resolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");
        var first = await ClaimAsync(10);
        await ExecuteAsync(
            Factory,
            "UPDATE support.ticket_surveys SET locked_until = now() - interval '1 second' WHERE ticket_id = @id",
            ("id", Id(resolved)));
        var second = await ClaimAsync(10);
        var surveyId = Assert.Single(first.SurveyIds);
        Assert.Equal(surveyId, Assert.Single(second.SurveyIds));

        await ProcessAsync(surveyId, first.ClaimedUntil);
        Assert.Empty(_email.Sent);
        await ProcessAsync(surveyId, second.ClaimedUntil);
        await ProcessAsync(surveyId, second.ClaimedUntil);

        Assert.Single(_email.Sent);
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveySent, Id(resolved).ToString()));
    }

    // Revisión del bloque C: cada encuesta renueva su reclamo justo antes de mandarse, así el reclamo cubre su propio envío aunque las
    // anteriores del lote hayan tardado.
    [Fact]
    public async Task EachSurveyRenewsItsClaimRightBeforeSending()
    {
        var scenario = await StartAsync(Factory);
        await EnableSurveyAsync(scenario);
        var resolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");
        var batch = await ClaimAsync(10);
        string? lockedAtSend = null;
        _email.OnSend = async _ => lockedAtSend = await TextAsync(
            Factory,
            "SELECT (locked_until > @claimed)::text FROM support.ticket_surveys WHERE ticket_id = @id",
            ("claimed", batch.ClaimedUntil),
            ("id", Id(resolved)));

        await ProcessAsync(Assert.Single(batch.SurveyIds), batch.ClaimedUntil);

        Assert.Equal("true", lockedAtSend);
        Assert.Single(_email.Sent);
        Assert.Equal(5, new Modules.Support.Infrastructure.SupportWorkerOptions().SurveyBatchSize);
    }

    // Revisión del bloque C: una excepción inesperada al mandar no deja la encuesta reclamada para volver a lanzar cada dos minutos para
    // siempre. El worker la pasa a reintento en un scope nuevo (1, 5 y 15 minutos) y, después del último, queda Failed y auditada.
    [Fact]
    public async Task ASurveyWhoseSendThrowsIsRetriedAndThenFails()
    {
        var scenario = await StartAsync(Factory);
        await EnableSurveyAsync(scenario);
        var resolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");
        _email.Throw = new InvalidOperationException("El canal se cayó.");
        var worker = ActivatorUtilities.CreateInstance<Modules.Support.Infrastructure.SupportSurveyWorker>(Factory.Services);

        Assert.Equal(1, await worker.RunOnceAsync(10, Ct));

        Assert.Equal(
            "Pending|send_failed|1|true|true",
            await TextAsync(
                Factory,
                """
                SELECT status || '|' || reason_code || '|' || attempts || '|' || (locked_until IS NULL)::text || '|'
                    || (next_attempt_at > now() + interval '50 seconds')::text
                FROM support.ticket_surveys WHERE ticket_id = @id
                """,
                ("id", Id(resolved))));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await ExecuteAsync(
                Factory, "UPDATE support.ticket_surveys SET next_attempt_at = now() WHERE ticket_id = @id", ("id", Id(resolved)));
            Assert.Equal(1, await worker.RunOnceAsync(10, Ct));
        }

        Assert.Equal(
            "Failed|send_failed|4",
            await TextAsync(
                Factory,
                "SELECT status || '|' || reason_code || '|' || attempts FROM support.ticket_surveys WHERE ticket_id = @id",
                ("id", Id(resolved))));
        Assert.Equal(0, await worker.RunOnceAsync(10, Ct));
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveyFailed, Id(resolved).ToString()));
        Assert.Empty(_email.Sent);
    }

    // Tarea 12 (H18): el toque de un botón que cita la encuesta la califica, con ese mensaje, y abre 24 horas para el comentario
    // contadas desde el toque. La auditoría es del sistema y nombra sólo el campo.
    [Fact]
    public async Task ATapOnTheSurveyRatesItWithItsMessage()
    {
        var sent = await SentWhatsAppSurveyAsync();
        var tappedAt = DateTimeOffset.UtcNow;
        var tap = await TapAsync(sent, "  buena ", tappedAt);

        await AnswersTickAsync();

        Assert.Equal(
            $"Good|WhatsApp|{tap}|true",
            await TextAsync(
                Factory,
                """
                SELECT rating || '|' || answered_via || '|' || rating_message_id || '|'
                    || (abs(extract(epoch FROM comment_until - (@tapped::timestamptz + interval '24 hours'))) < 1)::text
                FROM support.ticket_surveys WHERE id = @id
                """,
                ("tapped", tappedAt),
                ("id", sent.SurveyId)));
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveyAnswered, sent.TicketId.ToString()));
    }

    // Review Focus 7 (Q-11): un solo mensaje que pide el comentario, con un ClientId fijo por encuesta. Un tick que se repite no lo manda
    // otra vez; si el proceso murió antes de anotarlo, el siguiente lo repite con el mismo ClientId (Mensajería devuelve el que ya salió).
    [Fact]
    public async Task ThePromptIsSentOnceAfterTheTap()
    {
        var sent = await SentWhatsAppSurveyAsync();
        var prompt = Guid.CreateVersion7();
        _channels.OnThread = send => new SupportChannelSendResult.Sent(prompt, send.ConversationId);
        await TapAsync(sent, "Regular", DateTimeOffset.UtcNow);

        await AnswersTickAsync();
        await AnswersTickAsync();

        var send = Assert.Single(_channels.ThreadSends);
        Assert.Equal(
            (sent.Conversation, SupportSurveyPrompt.ClientIdFor(sent.SurveyId), SupportSurveyPrompt.Text, (Guid?)null),
            (send.ConversationId, send.ClientId, send.Text, send.UploadId));
        Assert.Equal("Gracias. Si quieres, cuéntanos en un mensaje por qué elegiste esa opción.", send.Text);
        Assert.Equal(
            $"{prompt}|true|",
            await TextAsync(
                Factory,
                """
                SELECT prompt_message_id || '|' || (prompt_sent_at IS NOT NULL)::text || '|' || coalesce(prompt_failure_code, '')
                FROM support.ticket_surveys WHERE id = @id
                """,
                ("id", sent.SurveyId)));

        // El proceso murió entre el envío y la anotación: el siguiente tick lo repite con el mismo ClientId.
        await ExecuteAsync(
            Factory,
            "UPDATE support.ticket_surveys SET prompt_message_id = NULL, prompt_sent_at = NULL WHERE id = @id",
            ("id", sent.SurveyId));
        await AnswersTickAsync();

        Assert.Equal(2, _channels.ThreadSends.Count);
        Assert.All(_channels.ThreadSends, item => Assert.Equal(SupportSurveyPrompt.ClientIdFor(sent.SurveyId), item.ClientId));
    }

    // Review Focus 7: el mensaje sale por el envío de respuestas de Soporte, no como respuesta del asesor: la conversación sigue sin
    // asignar, en su estado y con sus no leídos.
    [Fact]
    public async Task ThePromptDoesNotTakeTheConversation()
    {
        var sent = await SentWhatsAppSurveyAsync();
        var before = await ConversationStateAsync(sent.Conversation);
        await TapAsync(sent, "Mala", DateTimeOffset.UtcNow);

        await AnswersTickAsync();

        Assert.Single(_channels.ThreadSends);
        Assert.Empty(_channels.TemplateSends);
        Assert.Equal(before, await ConversationStateAsync(sent.Conversation));
        Assert.Equal("|Open|0", before);
    }

    // Q-11: sin reintento ni recordatorio. Si el canal no lo manda, queda el motivo y no se vuelve a intentar.
    [Fact]
    public async Task AFailedPromptIsNotRetried()
    {
        var sent = await SentWhatsAppSurveyAsync();
        _channels.OnThread = _ => new SupportChannelSendResult.Rejected(SupportChannelReasons.WindowClosed);
        await TapAsync(sent, "Buena", DateTimeOffset.UtcNow);

        await AnswersTickAsync();
        await AnswersTickAsync();

        Assert.Single(_channels.ThreadSends);
        Assert.Equal(
            "Good|window_closed|false",
            await TextAsync(
                Factory,
                "SELECT rating || '|' || prompt_failure_code || '|' || (prompt_message_id IS NOT NULL)::text FROM support.ticket_surveys WHERE id = @id",
                ("id", sent.SurveyId)));
    }

    // H18: el primer texto del cliente después del toque es el comentario; el siguiente es un entrante cualquiera.
    [Fact]
    public async Task TheNextTextIsTheCommentAndALaterOneIsNot()
    {
        var sent = await SentWhatsAppSurveyAsync();
        var tappedAt = DateTimeOffset.UtcNow;
        await TapAsync(sent, "Regular", tappedAt);
        await AnswersTickAsync();
        var comment = await SeedMessageAsync(
            Factory, sent.TenantId, sent.Conversation, tappedAt.AddMinutes(1), text: "  Tardaron mucho en responder.  ");
        await SeedMessageAsync(Factory, sent.TenantId, sent.Conversation, tappedAt.AddMinutes(2), text: "Otra cosa: ¿tienen domicilio?");

        await AnswersTickAsync();
        await AnswersTickAsync();

        Assert.Equal(
            $"Tardaron mucho en responder.|{comment}",
            await TextAsync(
                Factory, "SELECT comment || '|' || comment_message_id FROM support.ticket_surveys WHERE id = @id", ("id", sent.SurveyId)));
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveyCommented, sent.TicketId.ToString()));
    }

    // H18: pasadas las 24 horas del toque ya no se espera comentario.
    [Fact]
    public async Task ACommentAfterTheWindowIsIgnored()
    {
        var sent = await SentWhatsAppSurveyAsync();
        var tappedAt = DateTimeOffset.UtcNow;
        await TapAsync(sent, "Buena", tappedAt);
        await AnswersTickAsync();
        await ExecuteAsync(
            Factory, "UPDATE support.ticket_surveys SET comment_until = now() - interval '1 minute' WHERE id = @id", ("id", sent.SurveyId));
        await SeedMessageAsync(Factory, sent.TenantId, sent.Conversation, tappedAt.AddMinutes(1), text: "Muy amables.");

        await AnswersTickAsync();

        Assert.Equal(
            "Good|",
            await TextAsync(
                Factory, "SELECT rating || '|' || coalesce(comment, '') FROM support.ticket_surveys WHERE id = @id", ("id", sent.SurveyId)));
    }

    // Revisión del bloque D: la ventana se mide con la hora del mensaje. Un texto escrito dentro de las 24 horas del toque es el
    // comentario aunque el worker lo procese cuando la ventana ya cerró.
    [Fact]
    public async Task ACommentWrittenInTheWindowCountsEvenIfProcessedLate()
    {
        var sent = await SentWhatsAppSurveyAsync();
        var tap = await TapAsync(sent, "Buena", DateTimeOffset.UtcNow);
        await AnswersTickAsync();
        await ExecuteAsync(
            Factory, "UPDATE messaging.messages SET occurred_at = now() - interval '3 hours' WHERE id = @id", ("id", tap));
        await ExecuteAsync(
            Factory, "UPDATE support.ticket_surveys SET comment_until = now() - interval '1 hour' WHERE id = @id", ("id", sent.SurveyId));
        var comment = await SeedMessageAsync(
            Factory, sent.TenantId, sent.Conversation, DateTimeOffset.UtcNow.AddHours(-2), text: "Escrito a tiempo.");

        await AnswersTickAsync();

        Assert.Equal(
            $"Escrito a tiempo.|{comment}",
            await TextAsync(
                Factory, "SELECT comment || '|' || comment_message_id FROM support.ticket_surveys WHERE id = @id", ("id", sent.SurveyId)));
    }

    // Review Focus 3: el toque cuenta sólo para la encuesta que cita. Reabierto y vuelto a resolver, la primera ya no se responde: un
    // toque sobre ella no califica nada, ni la primera ni la segunda.
    [Fact]
    public async Task ATapOnAnOldSurveyRatesNothing()
    {
        var sent = await SentWhatsAppSurveyAsync();
        var reopened = await sent.Scenario.MoveAsync(await sent.Scenario.DetailAsync(sent.TicketId), "Open");
        await sent.Scenario.MoveAsync(reopened, "Resolved");
        await ProcessClaimedAsync();
        await TapAsync(sent, "Buena", DateTimeOffset.UtcNow);

        await AnswersTickAsync();

        Assert.Equal(
            "1:|2:",
            await TextAsync(
                Factory,
                """
                SELECT string_agg(resolution_number || ':' || coalesce(rating, ''), '|' ORDER BY resolution_number)
                FROM support.ticket_surveys WHERE ticket_id = @id
                """,
                ("id", sent.TicketId)));
        Assert.Empty(_channels.ThreadSends);
    }

    // Un toque cuyo texto no es uno de los tres botones no califica ni pide comentario.
    [Fact]
    public async Task ATapWithAnUnknownTextRatesNothing()
    {
        var sent = await SentWhatsAppSurveyAsync();
        await TapAsync(sent, "Excelente", DateTimeOffset.UtcNow);

        await AnswersTickAsync();

        Assert.Equal(
            "", await TextAsync(Factory, "SELECT coalesce(rating, '') FROM support.ticket_surveys WHERE id = @id", ("id", sent.SurveyId)));
        Assert.Empty(_channels.ThreadSends);
    }

    // H21: la primera calificación gana; otro toque sobre la misma encuesta no la cambia.
    [Fact]
    public async Task ATapRatesOnlyOnce()
    {
        var sent = await SentWhatsAppSurveyAsync();
        var tappedAt = DateTimeOffset.UtcNow;
        await TapAsync(sent, "Buena", tappedAt);
        await TapAsync(sent, "Mala", tappedAt.AddSeconds(5));

        await AnswersTickAsync();
        await AnswersTickAsync();

        Assert.Equal(
            "Good", await TextAsync(Factory, "SELECT rating FROM support.ticket_surveys WHERE id = @id", ("id", sent.SurveyId)));
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveyAnswered, sent.TicketId.ToString()));
        Assert.Single(_channels.ThreadSends);
    }

    // La calificación no sube la versión del ticket, pero avisa por el hub después del commit.
    [Fact]
    public async Task AnAnswerAnnouncesTheTicket()
    {
        var sent = await SentWhatsAppSurveyAsync();
        var version = Version(await sent.Scenario.DetailAsync(sent.TicketId));
        _realtime.Clear();
        await TapAsync(sent, "Buena", DateTimeOffset.UtcNow);

        await AnswersTickAsync();

        Assert.Contains(sent.TicketId, _realtime.Tickets);
        Assert.Equal(version, Version(await sent.Scenario.DetailAsync(sent.TicketId)));
    }

    // Un ticket abierto desde una conversación de WhatsApp, resuelto, con la encuesta ya mandada por la plantilla (el mensaje queda en
    // el hilo con el id que devolvió el canal).
    private async Task<SentSurvey> SentWhatsAppSurveyAsync()
    {
        var scenario = await StartAsync(Factory);
        await EnableSurveyAsync(scenario);
        var connection = Guid.CreateVersion7();
        var conversation = await SeedConversationAsync(Factory, scenario.TenantId, connectionId: connection);
        _channels.Threads[conversation] = new SupportThreadStatus(
            conversation, "WhatsApp", false, DateTimeOffset.UtcNow.AddHours(5), connection);
        _channels.ApprovedSurveyConnections[connection] = "Ventas";
        _channels.OnSurvey = send => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), send.ConversationId);
        var ticket = await scenario.CreateFromConversationAsync(conversation);
        await scenario.MoveAsync(ticket, "Resolved");
        await ProcessClaimedAsync();
        var send = Assert.Single(_channels.SurveySends);
        var surveyMessage = await TextAsync(
            Factory, "SELECT message_id::text FROM support.ticket_surveys WHERE id = @id", ("id", send.ClientId));
        await SeedMessageAsync(
            Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow, direction: Outbound, text: "¿Cómo te atendimos?",
            messageId: Guid.Parse(surveyMessage!, System.Globalization.CultureInfo.InvariantCulture));
        _channels.ThreadSends.Clear();
        return new SentSurvey(
            scenario, scenario.TenantId, Id(ticket), conversation, send.ClientId,
            Guid.Parse(surveyMessage!, System.Globalization.CultureInfo.InvariantCulture));
    }

    private Task<Guid> TapAsync(SentSurvey sent, string text, DateTimeOffset occurredAt) =>
        SeedMessageAsync(
            Factory, sent.TenantId, sent.Conversation, occurredAt, text: text, kind: InteractiveKind, replyTo: sent.SurveyMessage);

    private Task<string?> ConversationStateAsync(Guid conversation) =>
        TextAsync(
            Factory,
            "SELECT coalesce(assigned_member_id::text, '') || '|' || status || '|' || unread_count FROM messaging.conversations WHERE id = @id",
            ("id", conversation));

    // Un tick de las respuestas: los toques, los mensajes que piden el comentario y los comentarios.
    private async Task AnswersTickAsync() =>
        await ActivatorUtilities.CreateInstance<Modules.Support.Infrastructure.SupportSurveyWorker>(Factory.Services)
            .RunAnswersOnceAsync(Ct);

    private sealed record SentSurvey(
        SupportTicketScenario Scenario, Guid TenantId, Guid TicketId, Guid Conversation, Guid SurveyId, Guid SurveyMessage);

    // The tenant's settings row is created on first access (SupportProvisioner), and StartAsync touches no Support endpoint: without
    // provisioning first, the UPDATE matches no row and every survey is skipped as survey_disabled.
    private async Task EnableSurveyAsync(SupportTicketScenario scenario)
    {
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISupportProvisioner>().EnsureAsync(scenario.TenantId, Ct);
        }

        await ExecuteAsync(
            Factory, "UPDATE support.tenant_settings SET survey_enabled = true WHERE tenant_id = @tenant", ("tenant", scenario.TenantId));
    }

    private async Task<SupportSurveyBatch> ClaimAsync(int take)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SupportSurveySender>().ClaimAsync(take, Ct);
    }

    private async Task ProcessAsync(Guid surveyId, DateTimeOffset claimedUntil)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SupportSurveySender>().ProcessAsync(surveyId, claimedUntil, Ct);
    }

    private async Task ProcessClaimedAsync()
    {
        var batch = await ClaimAsync(10);
        foreach (var id in batch.SurveyIds)
        {
            await ProcessAsync(id, batch.ClaimedUntil);
        }
    }

    private async Task<IReadOnlyList<Guid>> SurveyIdsAsync(SupportTicketScenario scenario)
    {
        var ids = await TextAsync(
            Factory,
            "SELECT string_agg(id::text, ',') FROM support.ticket_surveys WHERE tenant_id = @tenant",
            ("tenant", scenario.TenantId));
        return ids!.Split(',').Select(id => Guid.Parse(id, System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }

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
