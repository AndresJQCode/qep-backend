using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Wholesale.Application;
using Modules.Wholesale.Infrastructure.Messaging;
using Testcontainers.PostgreSql;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>
/// Spec 2026-11-18: el aviso por WhatsApp de la invitación de punta a punta, desde el <c>POST</c> hasta el estado que pinta el
/// detalle. Integrations y Mensajería quedan detrás de fakes (las conexiones listas y el envío): lo que se prueba es Mayorista,
/// su evento con link (spec 2026-11-19 §2), el viejo sin link que todavía se drena, su inbox y su procesador. El worker se saca y el procesador se corre a mano, como en Compras, para que
/// las pruebas no dependan del sondeo.
/// </summary>
public sealed class DistributorWhatsAppNoticeApiTests(DistributorWhatsAppNoticeApiTests.NoticeHost host)
    : IClassFixture<DistributorWhatsAppNoticeApiTests.NoticeHost>
{
    /// <summary>El aviso viejo, sin link: ya nadie lo escribe, pero el procesador lo sigue drenando.</summary>
    private const string NoticeEvent = "wholesale.distributor-invitation-notice.v1";

    /// <summary>Spec 2026-11-19 §2: el aviso con link, el que escriben invitar y reenviar.</summary>
    private const string LinkEvent = "wholesale.distributor-invitation-link.v1";
    private const string RawPhone = "3001234567";
    private const string Phone = "+573001234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleApiHarness.QepApiFactory Factory => host.Factory;

    [Fact]
    public async Task InvitingWithAPhoneWritesTheLinkEventAndNotTheOldNotice()
    {
        var world = await ArrangeAsync(connections: 1);
        var connection = world.Connections[0];

        var response = await InviteAsync(world, RawPhone, connectionId: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Equal(Phone, created.Phone);
        Assert.Equal("Pending", created.Invitation.WhatsAppNotice?.Status);
        Assert.Equal(1, await CountEventsAsync(WholesaleApiHarness.DistributorInvitedEvent, created.Id));
        Assert.Equal(1, await CountEventsAsync(LinkEvent, created.Id));
        Assert.Equal(0, await CountEventsAsync(NoticeEvent, created.Id));

        // El evento nuevo lleva el mismo token que el correo; sin vencimiento y, spec 2026-11-24, sin la prueba de buzón.
        var token = await WholesaleApiHarness.LatestTokenAsync(Factory, created.Id);
        var emailProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, created.Id);
        Assert.NotNull(emailProof);
        var (noticeId, payload) = await NoticeAsync(created.Id);
        using (var document = JsonDocument.Parse(payload))
        {
            Assert.False(document.RootElement.TryGetProperty("expiresAt", out _));
            Assert.False(document.RootElement.TryGetProperty("emailProof", out _));
            Assert.Equal(token, document.RootElement.GetProperty("token").GetString());
        }

        Assert.DoesNotContain(emailProof, payload, StringComparison.Ordinal);

        await ProcessNoticesAsync();
        var detail = await WholesaleApiHarness.EventuallyAsync(
            () => DetailAsync(world, created.Id), read => read.Invitation.WhatsAppNotice?.Status == "Sent");

        Assert.Equal(("Sent", (string?)null, connection.Label),
            (detail.Invitation.WhatsAppNotice!.Status, detail.Invitation.WhatsAppNotice.FailureCode,
                detail.Invitation.WhatsAppNotice.ConnectionLabel));
        // Review Focus 3 (D-3): el resultado no sube la versión; el If-Match del POST sigue valiendo.
        Assert.Equal(created.Version, detail.Version);
        // D-5: el clientId del envío es el id del mensaje del outbox.
        var sent = Assert.Single(host.Sender.CallsFor(world.Tenant.TenantId));
        Assert.Equal(
            (noticeId, connection.Id, world.Tenant.MemberId, Phone, "Ana Pérez", "ana@distri.co"),
            (sent.MessageId, sent.ConnectionId, sent.SentByMemberId, sent.Phone, sent.ContactName, sent.Email));
        // El link llega al adaptador armado como el del correo (AppBaseUrl).
        Assert.EndsWith($"/wholesale-invitations/{Uri.EscapeDataString(token)}", sent.Link, StringComparison.Ordinal);

        // Review Focus 4: la auditoría lleva el flag del aviso y nunca el teléfono.
        var audit = Assert.Single(await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT changed_fields::text AS "Value" FROM audit.entries
            WHERE tenant_id = {world.Tenant.TenantId} AND action = 'wholesale.distributor.invited' AND resource_id = {created.Id.ToString()}
            """));
        Assert.Contains("whatsAppNotice=true", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(RawPhone, audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAPhoneOnlyTheEmailIsQueued()
    {
        var world = await ArrangeAsync(connections: 1);

        var response = await InviteAsync(world, phone: null, connectionId: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Null(created.Phone);
        Assert.Null(created.Invitation.WhatsAppNotice);
        Assert.Equal(1, await CountEventsAsync(WholesaleApiHarness.DistributorInvitedEvent, created.Id));
        Assert.Equal(0, await CountEventsAsync(LinkEvent, created.Id));
        Assert.Equal(0, await CountEventsAsync(NoticeEvent, created.Id));

        await ProcessNoticesAsync();

        Assert.Empty(host.Sender.CallsFor(world.Tenant.TenantId));
    }

    [Fact]
    public async Task AFailureOfTheNoticeDoesNotChangeTheEmailNorTheInvitation()
    {
        var world = await ArrangeAsync(connections: 1);
        host.Sender.Answer(world.Tenant.TenantId, new WholesaleNoticeSendResult.Failed("template_not_approved"));
        var created = await InviteCreatedAsync(world);
        var token = await WholesaleApiHarness.LatestTokenAsync(Factory, created.Id);

        await ProcessNoticesAsync();
        var detail = await WholesaleApiHarness.EventuallyAsync(
            () => DetailAsync(world, created.Id), read => read.Invitation.WhatsAppNotice?.Status == "Failed");

        Assert.Equal("Invited", detail.Status);
        Assert.Equal(created.Email, detail.Email);
        using (var anonymous = WholesaleApiHarness.CreateAnonymous(Factory))
        {
            // El link del correo sigue sirviendo: el aviso fallido no rotó nada.
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(WholesaleApiHarness.InvitationUrl(token), Ct)).StatusCode);
        }

        Assert.Equal(("Failed", (string?)"template_not_approved"),
            (detail.Invitation.WhatsAppNotice!.Status, detail.Invitation.WhatsAppNotice.FailureCode));
        Assert.Equal(created.Version, detail.Version);
        Assert.Equal(created.Invitation.ExpiresAt, detail.Invitation.ExpiresAt);
        Assert.Equal(1, await CountEventsAsync(WholesaleApiHarness.DistributorInvitedEvent, created.Id));
        // Un fallo es un resultado, no una excepción: el mensaje queda procesado y no se vuelve a mandar.
        await ProcessNoticesAsync();
        Assert.Single(host.Sender.CallsFor(world.Tenant.TenantId));
    }

    [Fact]
    public async Task ResendWithAPhoneQueuesANewNoticeAndANewEmail()
    {
        var world = await ArrangeAsync(connections: 1);
        var created = await InviteCreatedAsync(world);
        await ProcessNoticesAsync();
        var first = await DetailAsync(world, created.Id);

        var response = await ResendAsync(world, first);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resent = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Equal("Pending", resent.Invitation.WhatsAppNotice?.Status);
        Assert.Equal(2, await CountEventsAsync(WholesaleApiHarness.DistributorInvitedEvent, created.Id));
        Assert.Equal(2, await CountEventsAsync(LinkEvent, created.Id));
        Assert.Equal(0, await CountEventsAsync(NoticeEvent, created.Id));
        Assert.Equal([0, 1], await GenerationsAsync(created.Id));

        await ProcessNoticesAsync();
        var detail = await WholesaleApiHarness.EventuallyAsync(
            () => DetailAsync(world, created.Id), read => read.Invitation.WhatsAppNotice?.Status == "Sent");

        Assert.Equal("Sent", detail.Invitation.WhatsAppNotice!.Status);
        Assert.Equal(resent.Version, detail.Version);
        Assert.Equal(2, host.Sender.CallsFor(world.Tenant.TenantId).Count);
    }

    [Fact]
    public async Task ResendWithTheConnectionNoLongerReadySendsTheEmailAndFailsTheNotice()
    {
        var world = await ArrangeAsync(connections: 1);
        var created = await InviteCreatedAsync(world);
        await ProcessNoticesAsync();
        var first = await DetailAsync(world, created.Id);
        host.Connections.SetReady(world.Tenant.TenantId);

        var response = await ResendAsync(world, first);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resent = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Equal(("Failed", (string?)"whatsapp_unavailable"),
            (resent.Invitation.WhatsAppNotice!.Status, resent.Invitation.WhatsAppNotice.FailureCode));
        Assert.Equal(2, await CountEventsAsync(WholesaleApiHarness.DistributorInvitedEvent, created.Id));
        Assert.Equal(1, await CountEventsAsync(LinkEvent, created.Id));
        var audit = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT changed_fields::text AS "Value" FROM audit.entries
            WHERE tenant_id = {world.Tenant.TenantId} AND action = 'wholesale.distributor.invitation_resent'
              AND resource_id = {created.Id.ToString()}
            """);
        Assert.Contains("whatsAppNotice=false", Assert.Single(audit), StringComparison.Ordinal);
    }

    // Review Focus 2 (D-4): el envío de la generación 0 está en vuelo cuando el PUT cambia el teléfono (generación 1). Su
    // resultado no pisa el aviso nuevo, que queda en null hasta el próximo «Reenviar» (P-3), y el mensaje queda procesado.
    [Fact]
    public async Task ANoticeQueuedBeforeAPhoneChangeIsNotSent()
    {
        var world = await ArrangeAsync(connections: 1);
        var created = await InviteCreatedAsync(world);
        var hold = host.Sender.Hold(world.Tenant.TenantId);
        var processing = ProcessNoticesAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var put = await UpdateAsync(world, created, "3109876543", connectionId: null);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Null(updated.Invitation.WhatsAppNotice);
        hold.Release.SetResult();
        await processing;

        var detail = await DetailAsync(world, created.Id);
        Assert.Null(detail.Invitation.WhatsAppNotice);
        Assert.Equal("+573109876543", detail.Phone);
        Assert.Equal(updated.Version, detail.Version);
        Assert.Equal(1, Assert.Single(await GenerationsAsync(created.Id, current: true)));
        var (noticeId, _) = await NoticeAsync(created.Id);
        Assert.True(await ProcessedAsync(noticeId));

        // Una vuelta más no lo manda otra vez.
        await ProcessNoticesAsync();
        Assert.Single(host.Sender.CallsFor(world.Tenant.TenantId));
    }

    // D-4: si el cambio llega antes de que el procesador tome el mensaje, el aviso viejo ni siquiera llega al envío.
    [Fact]
    public async Task ANoticeProcessedAfterAPhoneChangeNeverReachesTheSender()
    {
        var world = await ArrangeAsync(connections: 1);
        var created = await InviteCreatedAsync(world);
        (await UpdateAsync(world, created, "3109876543", connectionId: null)).EnsureSuccessStatusCode();

        await ProcessNoticesAsync();

        Assert.Empty(host.Sender.CallsFor(world.Tenant.TenantId));
        var (noticeId, _) = await NoticeAsync(created.Id);
        Assert.True(await ProcessedAsync(noticeId));
        Assert.Null((await DetailAsync(world, created.Id)).Invitation.WhatsAppNotice);
    }

    // D-9: el formulario reenvía el teléfono guardado con la conexión en null; eso no es un cambio.
    [Fact]
    public async Task AnUnchangedPutWithTheSavedPhoneKeepsTheNotice()
    {
        var world = await ArrangeAsync(connections: 1);
        var created = await InviteCreatedAsync(world);
        await ProcessNoticesAsync();
        var sent = await DetailAsync(world, created.Id);
        Assert.Equal("Sent", sent.Invitation.WhatsAppNotice?.Status);

        var put = await UpdateAsync(world, sent, sent.Phone, connectionId: null);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Equal(sent.Phone, updated.Phone);
        Assert.Equal(sent.Invitation.WhatsAppNotice, updated.Invitation.WhatsAppNotice);
        Assert.Equal(0, Assert.Single(await GenerationsAsync(created.Id, current: true)));
    }

    // Spec 2026-11-19 §2: un aviso viejo (sin link) que quedó en el outbox antes del despliegue se sigue procesando, con la misma
    // regla de generación y sin link. Se simula pasando el evento recién escrito a la forma vieja, sin token.
    [Fact]
    public async Task APendingOldNoticeIsStillProcessed()
    {
        var world = await ArrangeAsync(connections: 1);
        var created = await InviteCreatedAsync(world);
        var (noticeId, _) = await NoticeAsync(created.Id);
        await WholesaleApiHarness.ExecuteAsync(
            Factory,
            $"""
            UPDATE platform.outbox_messages SET event_name = {NoticeEvent}, payload = payload - 'token'
            WHERE id = {noticeId}
            """);

        await ProcessNoticesAsync();
        var detail = await WholesaleApiHarness.EventuallyAsync(
            () => DetailAsync(world, created.Id), read => read.Invitation.WhatsAppNotice?.Status == "Sent");

        Assert.Equal("Sent", detail.Invitation.WhatsAppNotice?.Status);
        Assert.Equal(created.Version, detail.Version);
        var sent = Assert.Single(host.Sender.CallsFor(world.Tenant.TenantId));
        Assert.Equal((noticeId, (string?)null), (sent.MessageId, sent.Link));
        Assert.True(await ProcessedAsync(noticeId));
    }

    [Fact]
    public async Task TwoReadyConnectionsWithoutChoiceIs422()
    {
        var world = await ArrangeAsync(connections: 2);

        var response = await InviteAsync(world, RawPhone, connectionId: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wholesale.distributor.whatsapp_connection_required", await WholesaleApiHarness.CodeOfAsync(response));
        var distributors = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""SELECT count(*)::int AS "Value" FROM wholesale.distributors WHERE tenant_id = {world.Tenant.TenantId}""");
        Assert.Equal(0, distributors[0]);

        var chosen = await InviteAsync(world, RawPhone, world.Connections[1].Id);

        Assert.Equal(HttpStatusCode.Created, chosen.StatusCode);
    }

    private sealed record World(
        WholesaleTestTenant Tenant, HttpClient Manager, Guid PriceListId, Guid CompanyId, IReadOnlyList<WholesaleWhatsAppConnection> Connections);

    private async Task<World> ArrangeAsync(int connections)
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        var ready = Enumerable.Range(1, connections)
            .Select(index => new WholesaleWhatsAppConnection(Guid.CreateVersion7(), $"+57 300 000 000{index}"))
            .ToArray();
        host.Connections.SetReady(tenant.TenantId, ready);
        return new World(tenant, manager, list.Id, company, ready);
    }

    private static async Task<HttpResponseMessage> InviteAsync(World world, string? phone, Guid? connectionId) =>
        await world.Manager.PostAsJsonAsync(
            WholesaleApiHarness.DistributorsUrl(world.Tenant.TenantId),
            new
            {
                cityId = await WholesaleApiHarness.MedellinAsync(world.Manager),
                email = "ana@distri.co",
                businessName = "Distribuciones Norte SAS",
                country = "CO",
                identificationType = "NIT",
                identificationNumber = "900123456",
                contactName = "Ana Pérez",
                priceListId = world.PriceListId,
                advisorId = world.Tenant.MemberId,
                billingCompanyId = world.CompanyId,
                phone,
                whatsAppConnectionId = connectionId,
            },
            Ct);

    private static async Task<DistributorDetailDto> InviteCreatedAsync(World world)
    {
        var response = await InviteAsync(world, RawPhone, connectionId: null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> UpdateAsync(World world, DistributorDetailDto distributor, string? phone, Guid? connectionId) =>
        world.Manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(HttpMethod.Put, $"{WholesaleApiHarness.DistributorsUrl(world.Tenant.TenantId)}/{distributor.Id}")
                {
                    Content = JsonContent.Create(new
                    {
                        priceListId = world.PriceListId,
                        advisorId = world.Tenant.MemberId,
                        billingCompanyId = world.CompanyId,
                        phone,
                        whatsAppConnectionId = connectionId,
                    }),
                },
                distributor.Version),
            Ct);

    private static Task<HttpResponseMessage> ResendAsync(World world, DistributorDetailDto distributor) =>
        world.Manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{WholesaleApiHarness.DistributorsUrl(world.Tenant.TenantId)}/{distributor.Id}/resend-invitation"),
                distributor.Version),
            Ct);

    private static async Task<DistributorDetailDto> DetailAsync(World world, Guid distributorId) =>
        (await world.Manager.GetFromJsonAsync<DistributorDetailDto>(
            $"{WholesaleApiHarness.DistributorsUrl(world.Tenant.TenantId)}/{distributorId}", Ct))!;

    /// <summary>Corre el procesador del aviso hasta vaciar el lote (precedente de Compras).</summary>
    private async Task ProcessNoticesAsync()
    {
        var processor = Factory.Services.GetRequiredService<InvitationNoticeProcessor>();
        while (await processor.ProcessBatchAsync(Ct))
        {
        }
    }

    private async Task<int> CountEventsAsync(string eventName, Guid distributorId) =>
        (await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM platform.outbox_messages
            WHERE event_name = {eventName} AND payload ->> 'distributorId' = {distributorId.ToString()}
            """))[0];

    /// <summary>El primer aviso del distribuidor: el id del mensaje y su payload como texto.</summary>
    private async Task<(Guid Id, string Payload)> NoticeAsync(Guid distributorId)
    {
        var ids = await WholesaleApiHarness.RowsAsync<Guid>(
            Factory,
            $"""
            SELECT id AS "Value" FROM platform.outbox_messages
            WHERE event_name IN ({LinkEvent}, {NoticeEvent}) AND payload ->> 'distributorId' = {distributorId.ToString()}
            ORDER BY occurred_at, id
            """);
        var payloads = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""SELECT payload::text AS "Value" FROM platform.outbox_messages WHERE id = {ids[0]}""");
        return (ids[0], payloads[0]);
    }

    /// <summary>Las generaciones de los avisos encolados, o con <paramref name="current"/> la de la invitación hoy.</summary>
    private async Task<IReadOnlyList<int>> GenerationsAsync(Guid distributorId, bool current = false) =>
        current
            ? await WholesaleApiHarness.RowsAsync<int>(
                Factory,
                $"""SELECT generation AS "Value" FROM wholesale.distributor_invitations WHERE distributor_id = {distributorId}""")
            : await WholesaleApiHarness.RowsAsync<int>(
                Factory,
                $"""
                SELECT (payload ->> 'generation')::int AS "Value" FROM platform.outbox_messages
                WHERE event_name = {LinkEvent} AND payload ->> 'distributorId' = {distributorId.ToString()}
                ORDER BY (payload ->> 'generation')::int
                """);

    private async Task<bool> ProcessedAsync(Guid messageId) =>
        (await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM wholesale.inbox_messages
            WHERE consumer = {InvitationNoticeProcessor.Consumer} AND message_id = {messageId} AND processed_at IS NOT NULL
            """))[0] == 1;

    /// <summary>Host propio de la clase: reemplaza las conexiones y el envío por fakes y saca el worker del aviso. Las pruebas
    /// lo comparten con un tenant cada una; los fakes responden por tenant.</summary>
    public sealed class NoticeHost : IAsyncLifetime
    {
        private PostgreSqlContainer? _database;
        private WholesaleApiHarness.QepApiFactory? _factory;

        internal FakeNoticeConnections Connections { get; } = new();

        internal FakeNoticeSender Sender { get; } = new();

        internal WholesaleApiHarness.QepApiFactory Factory =>
            _factory ?? throw new InvalidOperationException("The notice host has not been initialized.");

        public async ValueTask InitializeAsync()
        {
            _database = await WholesaleApiHarness.StartDatabaseAsync();
            _factory = new WholesaleApiHarness.QepApiFactory(_database.GetConnectionString(), services =>
            {
                services.RemoveAll<IWholesaleWhatsAppConnections>();
                services.AddSingleton<IWholesaleWhatsAppConnections>(Connections);
                services.RemoveAll<IWholesaleInvitationNoticeSender>();
                services.AddSingleton<IWholesaleInvitationNoticeSender>(Sender);
                // El worker sondea solo y competiría con ProcessNoticesAsync; el procesador (singleton) sigue registrado.
                var workers = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType == typeof(InvitationNoticeWorker))
                    .ToList();
                foreach (var descriptor in workers)
                {
                    services.Remove(descriptor);
                }
            });
        }

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
    }

    /// <summary>Las conexiones listas por tenant; <c>FindLabelAsync</c> sólo conoce las listas.</summary>
    internal sealed class FakeNoticeConnections : IWholesaleWhatsAppConnections
    {
        private readonly ConcurrentDictionary<Guid, IReadOnlyList<WholesaleWhatsAppConnection>> _ready = new();

        public void SetReady(Guid tenantId, params WholesaleWhatsAppConnection[] connections) => _ready[tenantId] = connections;

        public Task<IReadOnlyList<WholesaleWhatsAppConnection>> ListReadyAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(_ready.TryGetValue(tenantId, out var ready) ? ready : []);

        public Task<string?> FindLabelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(_ready.TryGetValue(tenantId, out var ready)
                ? ready.FirstOrDefault(connection => connection.Id == connectionId)?.Label
                : null);
    }

    /// <summary>Un envío retenido: <c>Entered</c> se cumple al llegar el envío, que espera a <c>Release</c>.</summary>
    internal sealed record HeldSend(TaskCompletionSource Entered, TaskCompletionSource Release);

    /// <summary>Anota cada envío y responde por tenant (<c>Sent</c> por defecto); puede retener el próximo envío de un tenant.</summary>
    internal sealed class FakeNoticeSender : IWholesaleInvitationNoticeSender
    {
        private readonly ConcurrentQueue<WholesaleInvitationNotice> _calls = new();
        private readonly ConcurrentDictionary<Guid, WholesaleNoticeSendResult> _answers = new();
        private readonly ConcurrentDictionary<Guid, HeldSend> _holds = new();

        public IReadOnlyList<WholesaleInvitationNotice> CallsFor(Guid tenantId) =>
            _calls.Where(call => call.TenantId == tenantId).ToList();

        public void Answer(Guid tenantId, WholesaleNoticeSendResult result) => _answers[tenantId] = result;

        public HeldSend Hold(Guid tenantId)
        {
            var hold = new HeldSend(
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _holds[tenantId] = hold;
            return hold;
        }

        public async Task<WholesaleNoticeSendResult> SendAsync(WholesaleInvitationNotice notice, CancellationToken cancellationToken)
        {
            _calls.Enqueue(notice);
            if (_holds.TryRemove(notice.TenantId, out var hold))
            {
                hold.Entered.SetResult();
                await hold.Release.Task.WaitAsync(cancellationToken);
            }

            return _answers.TryGetValue(notice.TenantId, out var answer) ? answer : new WholesaleNoticeSendResult.Sent();
        }
    }
}
