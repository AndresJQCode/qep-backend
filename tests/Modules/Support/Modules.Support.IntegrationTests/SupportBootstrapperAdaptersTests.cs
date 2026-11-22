using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
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

    // Fase 4b (H8): el bucket privado en memoria, para el almacén de documentos. Ninguna otra prueba de la clase toca Storage.
    private readonly InMemorySupportObjectStorage _storage = new();

    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    private QepApiFactory Factory => _factory ?? throw new InvalidOperationException("The host is not started.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => (_database, _factory) = await OpenOwnHostAsync(services =>
    {
        services.RemoveAll<IObjectStorage>();
        services.AddSingleton<IObjectStorage>(_storage);
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

    // Revisión final (I2): las dos lecturas del feed miran sólo lo que llegó dentro de Support:Workers:FeedLookbackHours (24 h por
    // defecto), por created_at, y no todo desde follow_since en cada tick. La ventana del ticket se sigue midiendo con occurred_at.
    [Fact]
    public async Task TheFeedSkipsInboundCreatedBeforeTheLookback()
    {
        var tenant = await NewSeededTenantAsync();
        var conversation = await SeedConversationAsync(Factory, tenant);
        await SupportTicketSeed.SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var stale = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(1));
        var fresh = await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(2));
        await ExecuteAsync(
            Factory, "UPDATE messaging.messages SET created_at = now() - interval '25 hours' WHERE id = @id", ("id", stale));

        var pending = await FeedAsync();

        Assert.Equal([fresh], pending.Select(item => item.MessageId));
    }

    [Fact]
    public async Task TheReassignableListSkipsUnclassifiedCreatedBeforeTheLookback()
    {
        var tenant = await NewSeededTenantAsync();
        var conversation = await SeedConversationAsync(Factory, tenant);
        await SupportTicketSeed.SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var stale = Guid.CreateVersion7();
        var fresh = Guid.CreateVersion7();
        await ExecuteAsync(
            Factory,
            """
            INSERT INTO support.unclassified_messages (message_id, tenant_id, conversation_id, occurred_at, created_at)
            VALUES (@stale, @tenant, @conversation, @first, now() - interval '25 hours'), (@fresh, @tenant, @conversation, @second, now())
            """,
            ("stale", stale), ("fresh", fresh), ("tenant", tenant), ("conversation", conversation),
            ("first", Since.AddMinutes(1)), ("second", Since.AddMinutes(2)));

        await using var scope = Factory.Services.CreateAsyncScope();
        var reassignable = await scope.ServiceProvider.GetRequiredService<ISupportThreadFeed>().ListReassignableUnclassifiedAsync(50, Ct);

        Assert.Equal([fresh], reassignable.Select(item => item.MessageId));
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

    // Revisión del bloque C (CM3): el adaptador real de SupportReplyChannels mira el estado de la conexión de la conversación en
    // Integrations. Sólo «Active» es operativa; pausada, de otro tenant o inexistente, no (y reply-options dice connection_unavailable).
    [Fact]
    public async Task TheThreadConnectionIsOperationalOnlyWhenActive()
    {
        var tenant = await NewSeededTenantAsync();
        var other = await NewSeededTenantAsync();
        var active = await SeedConnectionAsync(tenant, "Active");
        var paused = await SeedConnectionAsync(tenant, "Paused");
        var foreign = await SeedConnectionAsync(other, "Active");
        var onActive = await SeedConversationAsync(Factory, tenant, connectionId: active);
        var onPaused = await SeedConversationAsync(Factory, tenant, waId: "573005550001", connectionId: paused);
        var onForeign = await SeedConversationAsync(Factory, tenant, waId: "573005550002", connectionId: foreign);
        var onMissing = await SeedConversationAsync(Factory, tenant, waId: "573005550003");

        await using var scope = Factory.Services.CreateAsyncScope();
        var channels = scope.ServiceProvider.GetRequiredService<ISupportReplyChannels>();

        Assert.True((await channels.FindThreadAsync(tenant, onActive, Ct))!.ConnectionOperational);
        Assert.False((await channels.FindThreadAsync(tenant, onPaused, Ct))!.ConnectionOperational);
        Assert.False((await channels.FindThreadAsync(tenant, onForeign, Ct))!.ConnectionOperational);
        Assert.False((await channels.FindThreadAsync(tenant, onMissing, Ct))!.ConnectionOperational);
        Assert.Null(await channels.FindThreadAsync(other, onActive, Ct));
    }

    // Fase 3 (plan F21): el documento del formulario se compara normalizado (sin signos, en mayúsculas), con el tipo, contra la ficha
    // de Customers. Un fusionado no sale; un incompleto sí; un documento de otro tipo o de otro tenant, no.
    [Fact]
    public async Task FindIdsByDocumentUsesTheNormalizedNumberAndSkipsMerged()
    {
        var tenant = await NewSeededTenantAsync();
        var other = await NewSeededTenantAsync();
        var dotted = await SupportPublicHarness.SeedIdentifiedCustomerAsync(Factory, tenant, "Verde Puntos", "Nit", "900.123.456");
        var plain = await SupportPublicHarness.SeedIdentifiedCustomerAsync(Factory, tenant, "Verde Plano", "Nit", "900123456");
        await SupportPublicHarness.SeedIdentifiedCustomerAsync(Factory, tenant, "Verde Fusionado", "Nit", "900-123-456", mergedInto: plain);
        await SupportPublicHarness.SeedIdentifiedCustomerAsync(Factory, tenant, "Otro tipo", "Cc", "900123456");
        await SupportPublicHarness.SeedIdentifiedCustomerAsync(Factory, other, "Otro tenant", "Nit", "900123456");

        await using var scope = Factory.Services.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<ISupportCustomerLookup>();
        var ids = await lookup.FindIdsByDocumentAsync(tenant, "nit", " 900123456 ", Ct);
        var none = await lookup.FindIdsByDocumentAsync(tenant, "NIT", "111", Ct);

        Assert.Equal(2, ids.Count);
        Assert.Contains(dotted, ids);
        Assert.Contains(plain, ids);
        Assert.Empty(none);
    }

    // Spec 2026-11-15 §9.3 (plan L30): the company that answers on the portal comes from Companies through the Bootstrapper adapter:
    // name, the wire code of its document type and its tax id, active or not, always within the tenant.
    [Fact]
    public async Task TheOrganizationDirectoryMapsTheCompany()
    {
        var tenant = await NewSeededTenantAsync();
        var other = await NewSeededTenantAsync();
        var verde = await SupportPublicHarness.SeedCompanyAsync(Factory, tenant.ToString(), "Verde S.A.S.", "900123456");
        var azul = await SupportPublicHarness.SeedCompanyAsync(Factory, tenant.ToString(), "Azul Ltda.", "800111222");
        var off = await SupportPublicHarness.SeedCompanyAsync(Factory, tenant.ToString(), "Apagada S.A.", "700111222", active: false);
        var foreign = await SupportPublicHarness.SeedCompanyAsync(Factory, other.ToString(), "Ajena S.A.", "600111222");

        await using var scope = Factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ISupportOrganizationDirectory>();
        var found = await directory.FindAsync(tenant, verde, Ct);
        var inactive = await directory.FindAsync(tenant, off, Ct);
        var options = await directory.ListActiveAsync(tenant, Ct);

        // Fase 4b (H5): la dirección, el correo, el teléfono y la ciudad van al membrete. La siembra no trae contacto, y la ciudad de
        // una empresa colombiana sale de Geography por su id.
        Assert.NotNull(found);
        Assert.Equal((verde, "Verde S.A.S.", "NIT", "900123456", true), (found.Id, found.Name, found.DocumentTypeCode, found.DocumentNumber, found.IsActive));
        Assert.Equal((null, null, null), (found.Address, found.Email, found.Phone));
        Assert.False(string.IsNullOrWhiteSpace(found.CityName));
        Assert.NotNull(inactive);
        Assert.Equal((off, "Apagada S.A.", "NIT", "700111222", false), (inactive.Id, inactive.Name, inactive.DocumentTypeCode, inactive.DocumentNumber, inactive.IsActive));
        Assert.Null(await directory.FindAsync(tenant, foreign, Ct));
        Assert.Null(await directory.FindAsync(tenant, Guid.CreateVersion7(), Ct));
        Assert.Equal([azul, verde], options.Select(option => option.Id));
        Assert.All(options, option => Assert.True(option.IsActive));
    }

    // Fase 4b (H8): el PDF de la respuesta formal va al bucket privado bajo support/ (sin regla de ciclo de vida: 10 años de
    // retención), con una clave nueva por documento, y se lee de vuelta tal cual. La descarga firmada lleva la vida que pide quien llama.
    [Fact]
    public async Task TheDocumentStorageSavesUnderTheSupportPrefixAndReadsBack()
    {
        var tenant = Guid.CreateVersion7();
        byte[] content = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];

        await using var scope = Factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<ISupportDocumentStorage>();
        var key = await storage.SaveAsync(tenant, content, Ct);
        var other = await storage.SaveAsync(tenant, content, Ct);
        var read = await storage.ReadAsync(key, Ct);
        var url = await storage.CreateDownloadUrlAsync(key, "Respuesta-SOP-000001.pdf", TimeSpan.FromMinutes(5), Ct);

        Assert.Matches($@"^support/tenants/{tenant:N}/replies/\d{{4}}/\d{{2}}/[0-9a-f]{{32}}\.pdf$", key);
        Assert.NotEqual(key, other);
        Assert.Equal(content, read);
        Assert.Contains(key, url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(5), _storage.LastDownloadExpiry);

        await storage.DeleteAsync(key, Ct);
        // Best effort: borrar lo que ya no existe no lanza.
        await storage.DeleteAsync(key, Ct);
        Assert.Null(await _storage.StatAsync(key, Ct));
    }

    private async Task<Guid> SeedConnectionAsync(Guid tenant, string status)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            Factory,
            """
            INSERT INTO integrations.connections (id, tenant_id, provider_key, name, status, fields, created_at, created_by, updated_at, version)
            VALUES (@id, @tenant, 'whatsapp-cloud', @name, @status, '{}'::jsonb, now(), @member, now(), 1)
            """,
            ("id", id), ("tenant", tenant), ("name", $"Conexión {status} {id:N}"), ("status", status), ("member", Guid.CreateVersion7()));
        return id;
    }

    // Fase 4b, Tarea 12 (Review Focus 6): el feed de la encuesta lee messaging.messages por nombre (reply_to_message_id, kind, text,
    // direction, imported, occurred_at). Contra el esquema migrado: el toque que cita la encuesta mandada, y después el primer texto del
    // contacto como comentario. Un saliente, un importado o un texto de otra conversación no cuentan.
    [Fact]
    public async Task TheSurveyFeedRunsAgainstTheMigratedSchema()
    {
        var tenant = await NewSeededTenantAsync();
        var conversation = await SeedConversationAsync(Factory, tenant);
        var otherConversation = await SeedConversationAsync(Factory, tenant, waId: "573009998877");
        var ticket = await SupportTicketSeed.SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        await SupportTicketSeed.UpdateTicketAsync(
            Factory, tenant, ticket.Id,
            item => item.ChangeStatus(SupportTicketStatus.Resolved, null, DateTimeOffset.UtcNow.AddMinutes(-10), SupportTicketSeed.Clock));
        var surveyMessage = await SeedMessageAsync(
            Factory, tenant, conversation, DateTimeOffset.UtcNow.AddMinutes(-5), direction: Outbound, text: "¿Cómo te atendimos?");
        await ExecuteAsync(
            Factory,
            """
            UPDATE support.ticket_surveys
            SET status = 'Sent', channel = 'WhatsApp', message_id = @message, sent_at = now() - interval '5 minutes'
            WHERE ticket_id = @ticket
            """,
            ("message", surveyMessage),
            ("ticket", ticket.Id.Value));
        var surveyId = Guid.Parse(
            (await TextAsync(Factory, "SELECT id::text FROM support.ticket_surveys WHERE ticket_id = @ticket", ("ticket", ticket.Id.Value)))!,
            System.Globalization.CultureInfo.InvariantCulture);
        var tappedAt = DateTimeOffset.UtcNow.AddMinutes(-4);
        await SeedMessageAsync(
            Factory, tenant, conversation, tappedAt.AddSeconds(-1), imported: true, text: "Mala", kind: InteractiveKind, replyTo: surveyMessage);
        await SeedMessageAsync(
            Factory, tenant, conversation, tappedAt.AddSeconds(-1), direction: Outbound, text: "Mala", kind: InteractiveKind,
            replyTo: surveyMessage);
        var tap = await SeedMessageAsync(
            Factory, tenant, conversation, tappedAt, text: "Buena", kind: InteractiveKind, replyTo: surveyMessage);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var taps = await scope.ServiceProvider.GetRequiredService<ISupportSurveyFeed>().ListTapsAsync(50, Ct);

            var found = Assert.Single(taps);
            Assert.Equal((tenant, surveyId, tap, "Buena"), (found.TenantId, found.SurveyId, found.MessageId, found.Text));
            Assert.True((found.OccurredAt - tappedAt).Duration() < TimeSpan.FromMilliseconds(1));
        }

        await ExecuteAsync(
            Factory,
            """
            UPDATE support.ticket_surveys
            SET rating = 'Good', answered_via = 'WhatsApp', answered_at = now(), rating_message_id = @tap,
                comment_until = now() + interval '1 day'
            WHERE id = @id
            """,
            ("tap", tap),
            ("id", surveyId));
        await SeedMessageAsync(Factory, tenant, otherConversation, tappedAt.AddSeconds(30), text: "De otra conversación");
        await SeedMessageAsync(Factory, tenant, conversation, tappedAt.AddSeconds(40), direction: Outbound, text: "Gracias.");
        await SeedMessageAsync(Factory, tenant, conversation, tappedAt.AddSeconds(45), imported: true, text: "Importado");
        var comment = await SeedMessageAsync(Factory, tenant, conversation, tappedAt.AddMinutes(1), text: "Muy amables.");
        await SeedMessageAsync(Factory, tenant, conversation, tappedAt.AddMinutes(2), text: "Otra cosa.");

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var feed = scope.ServiceProvider.GetRequiredService<ISupportSurveyFeed>();

            Assert.Empty(await feed.ListTapsAsync(50, Ct));
            var found = Assert.Single(await feed.ListCommentsAsync(50, Ct));
            Assert.Equal((tenant, surveyId, comment, "Muy amables."), (found.TenantId, found.SurveyId, found.MessageId, found.Text));
            Assert.True((found.OccurredAt - tappedAt.AddMinutes(1)).Duration() < TimeSpan.FromMilliseconds(1));
        }
    }

    // Fase 4b, Tarea 14 (Review Focus 6): el feed del cierre automático lee support.ticket_messages, support.ticket_surveys y
    // messaging.messages por nombre. Contra el esquema migrado: cuenta sólo el entrante en vivo vinculado (Linked o Pinned) después de la
    // resolución; no un vínculo soltado, un importado, un saliente ni el toque de la encuesta.
    [Fact]
    public async Task TheAutoCloseFeedRunsAgainstTheMigratedSchema()
    {
        var tenant = await NewSeededTenantAsync();
        var conversation = await SeedConversationAsync(Factory, tenant);
        var ticket = await SupportTicketSeed.SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        var resolvedAt = Since.AddHours(1);
        await SupportTicketSeed.UpdateTicketAsync(
            Factory, tenant, ticket.Id, item => item.ChangeStatus(SupportTicketStatus.Resolved, null, resolvedAt, SupportTicketSeed.Clock));
        var surveyMessage = await SeedMessageAsync(Factory, tenant, conversation, resolvedAt.AddMinutes(5), direction: Outbound, text: "Encuesta");
        await ExecuteAsync(
            Factory,
            "UPDATE support.ticket_surveys SET status = 'Sent', channel = 'WhatsApp', message_id = @message, sent_at = @at WHERE ticket_id = @id",
            ("message", surveyMessage), ("at", resolvedAt.AddMinutes(5)), ("id", ticket.Id.Value));
        var reply = Since.AddDays(2);
        await LinkAsync(tenant, ticket.Id.Value, conversation, Since.AddMinutes(30), "Linked");
        await LinkAsync(tenant, ticket.Id.Value, conversation, reply, "Pinned");
        await LinkAsync(tenant, ticket.Id.Value, conversation, Since.AddDays(3), "Released");
        await LinkAsync(tenant, ticket.Id.Value, conversation, Since.AddDays(4), "Linked", imported: true);
        await LinkAsync(tenant, ticket.Id.Value, conversation, Since.AddDays(4), "Linked", direction: Outbound);
        await LinkAsync(tenant, ticket.Id.Value, conversation, Since.AddDays(5), "Linked", kind: InteractiveKind, replyTo: surveyMessage);

        await using var scope = Factory.Services.CreateAsyncScope();
        var feed = scope.ServiceProvider.GetRequiredService<ISupportAutoCloseFeed>();
        var candidate = Assert.Single(await feed.ListCandidatesAsync(50, null, Ct));
        var last = await feed.LastCustomerReplyAsync(tenant, ticket.Id.Value, resolvedAt, Ct);

        Assert.Equal((tenant, ticket.Id.Value), (candidate.TenantId, candidate.TicketId));
        Assert.True((candidate.ResolvedAt - resolvedAt).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.True((candidate.LastCustomerReplyAt!.Value - reply).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.True((last!.Value - reply).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Empty(await feed.ListCandidatesAsync(50, candidate, Ct));
    }

    // Revisión del bloque D: un toque cuyo texto no es uno de los tres botones nunca califica, así que no vuelve a salir en cada tick ni
    // le quita el lugar a los demás: con un lote de uno, sale el toque válido de la encuesta mandada después.
    [Fact]
    public async Task AnUnrecognisedTapDoesNotTakeTheBatch()
    {
        var tenant = await NewSeededTenantAsync();
        var (_, unknownConversation, unknownMessage) = await SentWhatsAppSurveyAsync(tenant, "573001110000", DateTimeOffset.UtcNow.AddMinutes(-30));
        var (validSurvey, validConversation, validMessage) = await SentWhatsAppSurveyAsync(tenant, "573002220000", DateTimeOffset.UtcNow.AddMinutes(-20));
        foreach (var text in new[] { "Excelente", "Hola", "👍" })
        {
            await SeedMessageAsync(
                Factory, tenant, unknownConversation, DateTimeOffset.UtcNow.AddMinutes(-10), text: text, kind: InteractiveKind,
                replyTo: unknownMessage);
        }

        var valid = await SeedMessageAsync(
            Factory, tenant, validConversation, DateTimeOffset.UtcNow.AddMinutes(-5), text: " regular ", kind: InteractiveKind,
            replyTo: validMessage);

        await using var scope = Factory.Services.CreateAsyncScope();
        var tap = Assert.Single(await scope.ServiceProvider.GetRequiredService<ISupportSurveyFeed>().ListTapsAsync(1, Ct));

        Assert.Equal((validSurvey, valid), (tap.SurveyId, tap.MessageId));
    }

    // Revisión del bloque D: los recorridos del cierre automático y del feed de la encuesta tienen su índice parcial, con el mismo
    // predicado que la consulta (si el filtro de la consulta deja de implicarlo, Postgres recorre la tabla sin error).
    [Fact]
    public async Task TheAutoCloseAndSurveyScansHaveTheirPartialIndexes()
    {
        var indexes = await TextAsync(
            Factory,
            """
            SELECT string_agg(indexname || ' ' || indexdef, '|' ORDER BY indexname) FROM pg_indexes
            WHERE schemaname = 'support' AND indexname IN ('IX_tickets_resolved_auto_close', 'IX_ticket_surveys_awaiting_rating',
                'IX_ticket_surveys_awaiting_comment', 'IX_ticket_surveys_rating_message', 'IX_ticket_surveys_comment_message')
            """);

        var lines = indexes!.Split('|');
        Assert.Equal(5, lines.Length);
        // status is varchar: Postgres deparses the filter as ((status)::text = 'Resolved'::text).
        Assert.Contains(lines, line => line.StartsWith("IX_tickets_resolved_auto_close ", StringComparison.Ordinal)
            && line.Contains("(resolved_at, id) WHERE", StringComparison.Ordinal)
            && line.Contains("(status)::text = 'Resolved'::text", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("IX_ticket_surveys_awaiting_rating ", StringComparison.Ordinal)
            && line.Contains("(sent_at, id) WHERE", StringComparison.Ordinal) && line.Contains("rating IS NULL", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("IX_ticket_surveys_awaiting_comment ", StringComparison.Ordinal)
            && line.Contains("(answered_at, id) WHERE", StringComparison.Ordinal) && line.Contains("comment IS NULL", StringComparison.Ordinal));
    }

    // Un ticket de una conversación nueva, resuelto, con la encuesta mandada por WhatsApp en sentAt.
    private async Task<(Guid SurveyId, Guid Conversation, Guid SurveyMessage)> SentWhatsAppSurveyAsync(
        Guid tenant, string waId, DateTimeOffset sentAt)
    {
        var conversation = await SeedConversationAsync(Factory, tenant, waId: waId);
        var ticket = await SupportTicketSeed.SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        await SupportTicketSeed.UpdateTicketAsync(
            Factory, tenant, ticket.Id,
            item => item.ChangeStatus(SupportTicketStatus.Resolved, null, sentAt.AddMinutes(-1), SupportTicketSeed.Clock));
        var message = await SeedMessageAsync(Factory, tenant, conversation, sentAt, direction: Outbound, text: "Encuesta");
        await ExecuteAsync(
            Factory,
            "UPDATE support.ticket_surveys SET status = 'Sent', channel = 'WhatsApp', message_id = @message, sent_at = @at WHERE ticket_id = @id",
            ("message", message), ("at", sentAt), ("id", ticket.Id.Value));
        var survey = Guid.Parse(
            (await TextAsync(Factory, "SELECT id::text FROM support.ticket_surveys WHERE ticket_id = @id", ("id", ticket.Id.Value)))!,
            System.Globalization.CultureInfo.InvariantCulture);
        return (survey, conversation, message);
    }

    // Un mensaje de la conversación vinculado al ticket en el estado pedido, como lo dejaría el seguimiento del hilo.
    private async Task LinkAsync(
        Guid tenant,
        Guid ticketId,
        Guid conversation,
        DateTimeOffset occurredAt,
        string state,
        bool imported = false,
        short direction = Inbound,
        short kind = TextKind,
        Guid? replyTo = null)
    {
        var message = await SeedMessageAsync(
            Factory, tenant, conversation, occurredAt, direction: direction, imported: imported, kind: kind, replyTo: replyTo);
        await ExecuteAsync(
            Factory,
            """
            INSERT INTO support.ticket_messages (id, tenant_id, ticket_id, conversation_id, message_id, state, occurred_at, created_at, updated_at)
            VALUES (gen_random_uuid(), @tenant, @ticket, @conversation, @message, @state, @occurred, now(), now())
            """,
            ("tenant", tenant), ("ticket", ticketId), ("conversation", conversation), ("message", message), ("state", state),
            ("occurred", occurredAt));
    }

    // Spec 2026-11-22 §5.2 (Task 9): el conjunto exacto de GET /tasks/assignees y del responsable de una tarea.
    [Fact]
    public async Task TaskMembersAreActiveMembersWithTaskUseOrManage()
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(Factory, tenant);
        var cashier = await SeedMemberAsync(Factory, $"caja-{Guid.CreateVersion7():N}@example.com", "Cora Cajera", "cashier", tenantId: tenant);
        var removedCashier = await SeedMemberAsync(
            Factory, $"caja-{Guid.CreateVersion7():N}@example.com", "Quitada", "cashier", removed: true, tenantId: tenant);
        var advisor = await SeedMemberAsync(Factory, $"asesor-{Guid.CreateVersion7():N}@example.com", "Aldo Asesor", "advisor", tenantId: tenant);
        var other = NewTenantId();
        await SeedTenantAsync(Factory, other);
        var foreign = await SeedMemberAsync(Factory, $"otra-{Guid.CreateVersion7():N}@example.com", "De otra empresa", "cashier", tenantId: other);

        await using var scope = Factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ISupportMemberDirectory>();
        var members = await directory.ListTaskMembersAsync(Guid.Parse(tenant), Ct);

        Assert.Contains(members, member => member.MemberId == cashier && member.CanUseTasks);
        Assert.Contains(members, member => member.MemberId == advisor && member.CanManage);
        Assert.DoesNotContain(members, member => member.MemberId == removedCashier);
        Assert.DoesNotContain(members, member => member.MemberId == foreign);

        var found = await directory.FindAsync(Guid.Parse(tenant), [cashier, removedCashier], Ct);
        Assert.True(found.Single(member => member.MemberId == cashier).CanUseTasks);
        Assert.False(found.Single(member => member.MemberId == removedCashier).CanUseTasks);
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
