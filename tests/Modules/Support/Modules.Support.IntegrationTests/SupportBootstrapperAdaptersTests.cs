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

        Assert.Equal(new SupportOrganizationRef(verde, "Verde S.A.S.", "NIT", "900123456", true), found);
        Assert.Equal(new SupportOrganizationRef(off, "Apagada S.A.", "NIT", "700111222", false), inactive);
        Assert.Null(await directory.FindAsync(tenant, foreign, Ct));
        Assert.Null(await directory.FindAsync(tenant, Guid.CreateVersion7(), Ct));
        Assert.Equal([azul, verde], options.Select(option => option.Id));
        Assert.All(options, option => Assert.True(option.IsActive));
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
