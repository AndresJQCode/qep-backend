using Bootstrapper;
using Modules.Automations.Application;
using Modules.Automations.Domain.Engine;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-21 §5.3: los adaptadores que le dan a Automations lo de Mensajería e Integrations. El de nombres de
/// chatbots lee <c>AutomationsDbContext</c> y se prueba contra PostgreSQL en <c>MessageAuthorshipApiTests.TheThreadShowsTheRealChatbotName</c>
/// (el repo no usa EF en memoria).</summary>
public sealed class AutomationAdaptersTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListsWhatsAppAndInstagramWithHandles()
    {
        var whatsApp = Guid.CreateVersion7();
        var instagram = Guid.CreateVersion7();
        var paused = Guid.CreateVersion7();
        var directory = new StubDirectory(
            all:
            [
                new MessagingConnectionRef(whatsApp, "Ventas", ConversationChannel.WhatsApp, "Active"),
                new MessagingConnectionRef(paused, "Soporte", ConversationChannel.WhatsApp, "Paused"),
                new MessagingConnectionRef(instagram, "Tienda", ConversationChannel.Instagram, "Active"),
            ],
            active:
            [
                new MessagingConnectionDto(whatsApp, "Ventas", "+57 300 1234567"),
                new MessagingConnectionDto(instagram, "Tienda", null, nameof(ConversationChannel.Instagram), "origenbotanico"),
            ]);

        var result = await new AutomationConnections(directory).ListAsync(TenantId, Ct);

        Assert.Equal(
            [
                (whatsApp, "WhatsApp", "+57 300 1234567", "Active"),
                (paused, "WhatsApp", string.Empty, "Paused"),
                (instagram, "Instagram", "@origenbotanico", "Active"),
            ],
            result.Select(connection => (connection.Id, connection.Channel, connection.Handle, connection.Status)));
    }

    [Fact]
    public async Task ExternalApisComeFromTheExternalApiProvider()
    {
        var active = Guid.CreateVersion7();
        var paused = Guid.CreateVersion7();
        var connections = new StubIntegrationConnections(
            [new ConnectionListing(active, "Clientes", ConnectionStatus.Active), new ConnectionListing(paused, "Órdenes", ConnectionStatus.Paused)]);

        var result = await new AutomationExternalApis(connections).ListAsync(TenantId, Ct);

        Assert.Equal("external-api", connections.RequestedProvider);
        Assert.Equal([(active, "Clientes", "Active"), (paused, "Órdenes", "Paused")], result.Select(api => (api.Id, api.Name, api.Status)));
    }

    /// <summary>D-H5: la clave sale de Integrations al momento de llamar; sin header guardado se usa <c>x-api-key</c>, como el probador.</summary>
    [Fact]
    public async Task ResolveReturnsTheCredentialsOfAnExternalApiConnection()
    {
        var id = Guid.CreateVersion7();
        var connections = new StubIntegrationConnections([], new ResolvedConnection(
            id, "external-api", "Clasic",
            new Dictionary<string, string> { ["baseUrl"] = "https://api.clasic.test/v1", ["authHeader"] = " " },
            new Dictionary<string, string> { ["apiKey"] = "KEY-SENTINEL" }));

        var credentials = await new AutomationExternalApis(connections).ResolveAsync(TenantId, id, Ct);

        Assert.Equal(
            (new Uri("https://api.clasic.test/v1"), "x-api-key", "KEY-SENTINEL"), (credentials?.BaseUrl, credentials?.AuthHeader, credentials?.ApiKey));
        Assert.DoesNotContain("KEY-SENTINEL", credentials!.ToString(), StringComparison.Ordinal);
        Assert.Equal((TenantId, id), connections.Resolved);
    }

    [Fact]
    public async Task ResolveKeepsTheStoredAuthHeader()
    {
        var id = Guid.CreateVersion7();
        var connections = new StubIntegrationConnections([], new ResolvedConnection(
            id, "external-api", "Clasic",
            new Dictionary<string, string> { ["baseUrl"] = "https://api.clasic.test", ["authHeader"] = "X-Token" },
            new Dictionary<string, string> { ["apiKey"] = "K" }));

        Assert.Equal("X-Token", (await new AutomationExternalApis(connections).ResolveAsync(TenantId, id, Ct))?.AuthHeader);
    }

    /// <summary>Una conexión de otro proveedor (un WhatsApp con el id de un bloque mal importado) no es una API externa.</summary>
    [Fact]
    public async Task ResolveIgnoresAnotherProviderAndAnUnresolvedConnection()
    {
        var other = new StubIntegrationConnections([], new ResolvedConnection(
            Guid.CreateVersion7(), "meta-whatsapp", "Ventas",
            new Dictionary<string, string> { ["baseUrl"] = "https://api.clasic.test" }, new Dictionary<string, string> { ["apiKey"] = "K" }));
        var none = new StubIntegrationConnections([]);

        Assert.Null(await new AutomationExternalApis(other).ResolveAsync(TenantId, Guid.CreateVersion7(), Ct));
        Assert.Null(await new AutomationExternalApis(none).ResolveAsync(TenantId, Guid.CreateVersion7(), Ct));
    }

    /// <summary>D-H2 y P2-R30/P2-R31: la política del bloque HTTP es la misma de Integrations, también al reconocer el rechazo al conectar.</summary>
    [Fact]
    public void TheUrlPolicyIsTheIntegrationsPolicy()
    {
        var policy = new AutomationExternalApiUrlPolicy();

        Assert.Equal("scheme_not_https", policy.Reject(new Uri("http://api.clasic.test")));
        Assert.Equal("host_is_ip_literal", policy.Reject(new Uri("https://10.0.0.1")));
        Assert.Null(policy.Reject(new Uri("https://api.clasic.test/v1")));
        Assert.True(policy.IsReservedHeader("host"));
        Assert.True(policy.IsReservedHeader("Content-Type"));
        Assert.False(policy.IsReservedHeader("x-api-key"));
        Assert.Equal(
            "resolved_to_forbidden_range",
            policy.RejectionIn(new HttpRequestException("x", new Modules.Integrations.Infrastructure.ExternalApi.ExternalApiUrlRejectedException("resolved_to_forbidden_range"))));
        Assert.Null(policy.RejectionIn(new HttpRequestException("x", new IOException("reset"))));
    }

    [Fact]
    public async Task ScheduleNamesCarryTheEffectiveScheduleOrNull()
    {
        var withSchedule = Guid.CreateVersion7();
        var without = Guid.CreateVersion7();
        var hours = new StubBusinessHours(new Dictionary<Guid, BusinessHoursStatus>
        {
            [withSchedule] = new(true, null, Guid.CreateVersion7(), "Horario comercial"),
            [without] = new(true, null, null, null),
        });

        var result = await new AutomationBusinessHours(hours).ScheduleNamesAsync(TenantId, [withSchedule, without], Ct);

        Assert.Equal("Horario comercial", result[withSchedule]);
        Assert.Null(result[without]);
    }

    [Fact]
    public async Task IsOpenComesFromTheConnectionStatus()
    {
        var open = Guid.CreateVersion7();
        var closed = Guid.CreateVersion7();
        var hours = new StubBusinessHours(new Dictionary<Guid, BusinessHoursStatus>
        {
            [open] = new(true, null, null, null),
            [closed] = new(false, null, Guid.CreateVersion7(), "Horario comercial"),
        });
        var adapter = new AutomationBusinessHours(hours);

        Assert.True(await adapter.IsOpenAsync(TenantId, open, Ct));
        Assert.False(await adapter.IsOpenAsync(TenantId, closed, Ct));
    }

    [Fact]
    public async Task TheMessengerMapsButtonsListAndCtaUrl()
    {
        var outbound = new RecordingBotOutbound(new BotSendResult.Sent(Guid.CreateVersion7(), AlreadySent: false));
        var messenger = new AutomationMessenger(outbound);
        var conversationId = Guid.CreateVersion7();
        var chatbotId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();

        await messenger.SendInteractiveAsync(
            TenantId, conversationId, chatbotId, clientId, new InteractiveMessage.Buttons("¿Qué necesitas?", [("sales", "Ventas"), ("tech", "Soporte")]), Ct);
        await messenger.SendInteractiveAsync(
            TenantId, conversationId, chatbotId, clientId,
            new InteractiveMessage.List("Elige una sede", "Ver sedes", [("bog", "Bogotá", "Calle 80"), ("med", "Medellín", null)]), Ct);
        await messenger.SendInteractiveAsync(
            TenantId, conversationId, chatbotId, clientId, new InteractiveMessage.CtaUrl("Paga aquí", "Pagar", "https://pagos.example.com/p/1"), Ct);
        await messenger.SendTextAsync(TenantId, conversationId, chatbotId, clientId, "Hola", Ct);

        Assert.All(outbound.Sends, send => Assert.Equal((TenantId, conversationId, chatbotId, clientId), (send.TenantId, send.ConversationId, send.ChatbotId, send.ClientId)));
        var buttons = Assert.IsType<ButtonsSend>(outbound.Sends[0].Interactive);
        Assert.Null(outbound.Sends[0].Text);
        Assert.Equal("¿Qué necesitas?", buttons.Body);
        Assert.Equal([new InteractiveButton("sales", "Ventas"), new InteractiveButton("tech", "Soporte")], buttons.Buttons);
        Assert.Null(buttons.Header);
        var list = Assert.IsType<ListSend>(outbound.Sends[1].Interactive);
        Assert.Equal(("Elige una sede", "Ver sedes"), (list.Body, list.ButtonLabel));
        var section = Assert.Single(list.Sections);
        Assert.Null(section.Title);
        Assert.Equal([new ListRow("bog", "Bogotá", "Calle 80"), new ListRow("med", "Medellín")], section.Rows);
        var cta = Assert.IsType<CtaUrlSend>(outbound.Sends[2].Interactive);
        Assert.Equal(("Paga aquí", "Pagar", new Uri("https://pagos.example.com/p/1")), (cta.Body, cta.DisplayText, cta.Url));
        Assert.Equal(("Hola", (InteractiveSend?)null), (outbound.Sends[3].Text, outbound.Sends[3].Interactive));
    }

    [Fact]
    public async Task ACtaUrlThatIsNotAnAbsoluteUriIsRejectedWithoutSending()
    {
        var outbound = new RecordingBotOutbound(new BotSendResult.Sent(Guid.CreateVersion7(), AlreadySent: false));

        // La URL sale de interpolar variables (D-I1): un {link} vacío deja un texto que no es una URL.
        var result = await new AutomationMessenger(outbound).SendInteractiveAsync(
            TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), new InteractiveMessage.CtaUrl("Paga aquí", "Pagar", "/p/1"), Ct);

        Assert.Equal(new BotSendOutcome.Rejected("messaging.interactive.invalid"), result);
        Assert.Empty(outbound.Sends);
    }

    [Fact]
    public async Task TheMessengerMapsEveryBotSendResult()
    {
        var messageId = Guid.CreateVersion7();
        (BotSendResult Result, BotSendOutcome Expected)[] cases =
        [
            (new BotSendResult.Sent(messageId, AlreadySent: false), new BotSendOutcome.Sent(messageId)),
            (new BotSendResult.Sent(messageId, AlreadySent: true), new BotSendOutcome.Sent(messageId)),
            (new BotSendResult.NotAutomated(), new BotSendOutcome.NotAutomated()),
            (new BotSendResult.WindowClosed(), new BotSendOutcome.WindowClosed()),
            (new BotSendResult.ConnectionUnavailable(), new BotSendOutcome.Rejected(MessagingErrorCodes.ConnectionUnavailable)),
            (new BotSendResult.Rejected("messaging.interactive.invalid"), new BotSendOutcome.Rejected("messaging.interactive.invalid")),
        ];

        foreach (var (result, expected) in cases)
        {
            var outcome = await new AutomationMessenger(new RecordingBotOutbound(result)).SendTextAsync(
                TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Hola", Ct);

            Assert.Equal(expected, outcome);
        }
    }

    [Fact]
    public async Task TheQueueMapsItemsAndInboundKinds()
    {
        var messageId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var createdAt = DateTimeOffset.UtcNow;
        var inbox = new StubAutomationInbox
        {
            Claimed = [new AutomationInboxItem(messageId, TenantId, connectionId, conversationId, true, createdAt, 2)],
            Inbound = new InboundForAutomation(messageId, MessageKind.Interactive, "Soporte técnico", "tech", null, createdAt),
        };
        var queue = new AutomationInboundQueue(inbox);

        var item = Assert.Single(await queue.ClaimAsync(createdAt, 20, Ct));
        var inbound = await queue.ReadAsync(TenantId, messageId, Ct);

        Assert.Equal(new InboundQueueItem(messageId, TenantId, connectionId, conversationId, true, createdAt, 2), item);
        Assert.Equal((createdAt, 20), inbox.ClaimedWith);
        Assert.Equal(new InboundMessage(messageId, "Interactive", "Soporte técnico", "tech", null, createdAt), inbound);

        inbox.Inbound = new InboundForAutomation(messageId, MessageKind.Document, "leyenda", null, new InboundMediaForAutomation("application/pdf", "f.pdf", false), createdAt);
        Assert.Equal(
            new InboundMessage(messageId, "Document", "leyenda", null, new InboundAttachment("application/pdf", "f.pdf", false), createdAt),
            await queue.ReadAsync(TenantId, messageId, Ct));

        inbox.Inbound = null;
        Assert.Null(await queue.ReadAsync(TenantId, messageId, Ct));
    }

    [Fact]
    public async Task TheQueuePassesTheFencingThrough()
    {
        var messageId = Guid.CreateVersion7();
        var inbox = new StubAutomationInbox { Accepts = false };
        var queue = new AutomationInboundQueue(inbox);

        Assert.False(await queue.CompleteAsync(TenantId, messageId, 3, "handled", Ct));
        Assert.False(await queue.RetryLaterAsync(TenantId, messageId, 3, TimeSpan.FromSeconds(2), Ct));
        inbox.Accepts = true;
        Assert.True(await queue.CompleteAsync(TenantId, messageId, 3, "handled", Ct));

        Assert.Equal(
            [$"complete {TenantId} {messageId} 3 handled", $"retry {TenantId} {messageId} 3 00:00:02", $"complete {TenantId} {messageId} 3 handled"],
            inbox.Calls);
    }

    /// <summary>P2-R7: la purga del inbox pasa tal cual a Mensajería y devuelve lo que borró.</summary>
    [Fact]
    public async Task TheQueuePassesThePurgeThrough()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var inbox = new StubAutomationInbox { Purged = 42 };

        Assert.Equal(42, await new AutomationInboundQueue(inbox).PurgeProcessedBeforeAsync(cutoff, 1000, Ct));

        Assert.Equal([$"purge {cutoff:O} 1000"], inbox.Calls);
    }

    [Fact]
    public async Task TheMediaAdapterMapsTheStoredFileAndTheStore()
    {
        var messageId = Guid.CreateVersion7();
        var reads = new StubMediaReads(new StoredMedia("tenants/t/m/1", "application/pdf", "f.pdf", 3));
        var store = new StubMediaStore();
        var media = new AutomationMedia(reads, store);

        var stored = await media.TryGetStoredAsync(TenantId, messageId, Ct);
        var url = await media.CreateDownloadUrlAsync("tenants/t/m/1", TimeSpan.FromMinutes(15), Ct);
        var opened = await media.OpenReadAsync("tenants/t/m/1", Ct);

        Assert.Equal(new StoredAttachment("tenants/t/m/1", "f.pdf", "application/pdf", 3), stored);
        Assert.Equal((TenantId, messageId), reads.AskedFor);
        Assert.Equal(new Uri("https://r2.test/tenants/t/m/1?minutes=15"), url);
        Assert.NotNull(opened);
        Assert.Equal(3, opened.Value.Length);
        await opened.Value.Content.DisposeAsync();
        Assert.Null(await media.OpenReadAsync("missing", Ct));
        Assert.Null(await new AutomationMedia(new StubMediaReads(null), store).TryGetStoredAsync(TenantId, messageId, Ct));
    }

    private sealed class RecordingBotOutbound(BotSendResult result) : IBotOutbound
    {
        public List<BotSend> Sends { get; } = [];

        public Task<BotSendResult> SendAsync(BotSend send, CancellationToken cancellationToken)
        {
            Sends.Add(send);
            return Task.FromResult(result);
        }
    }

    private sealed class StubAutomationInbox : IAutomationInbox
    {
        public IReadOnlyList<AutomationInboxItem> Claimed { get; init; } = [];

        public InboundForAutomation? Inbound { get; set; }

        public bool Accepts { get; set; } = true;

        public (DateTimeOffset Now, int Batch)? ClaimedWith { get; private set; }

        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<AutomationInboxItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken)
        {
            ClaimedWith = (now, batch);
            return Task.FromResult(Claimed);
        }

        public Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken)
        {
            Calls.Add($"complete {tenantId} {messageId} {attempts} {outcome}");
            return Task.FromResult(Accepts);
        }

        public Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken)
        {
            Calls.Add($"retry {tenantId} {messageId} {attempts} {delay}");
            return Task.FromResult(Accepts);
        }

        public Task<InboundForAutomation?> ReadInboundAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
            Task.FromResult(Inbound);

        public Task<Guid?> FindLastInboundIdAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public int Purged { get; init; }

        public Task<int> PurgeProcessedBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken)
        {
            Calls.Add($"purge {cutoff:O} {batch}");
            return Task.FromResult(Purged);
        }
    }

    private sealed class StubMediaReads(StoredMedia? stored) : IMediaReads
    {
        public (Guid TenantId, Guid MessageId)? AskedFor { get; private set; }

        public Task<StoredMedia?> FindStoredAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
        {
            AskedFor = (tenantId, messageId);
            return Task.FromResult(stored);
        }
    }

    private sealed class StubMediaStore : IMessagingMediaStore
    {
        public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(key == "missing" ? null : new MediaStreamDto(new MemoryStream([1, 2, 3]), "application/pdf", 3, "f.pdf"));

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}?minutes={lifetime.TotalMinutes}"));
    }

    private sealed class StubDirectory(IReadOnlyList<MessagingConnectionRef> all, IReadOnlyList<MessagingConnectionDto> active) : IMessagingConnectionDirectory
    {
        public Task<IReadOnlyList<MessagingConnectionRef>> ListAllAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(all);

        public Task<IReadOnlyList<MessagingConnectionDto>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(active);

        public Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MessagingRoute>> FindByAccountAsync(string wabaId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessagingSender?> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessagingTemplateSource?> ResolveTemplateSourceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessagingRoute?> FindInstagramRouteAsync(string igUserId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstagramSender?> ResolveInstagramSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConversationChannel?> FindChannelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubIntegrationConnections(IReadOnlyList<ConnectionListing> listings, ResolvedConnection? resolved = null) : IIntegrationConnections
    {
        public string? RequestedProvider { get; private set; }

        public (Guid TenantId, Guid ConnectionId)? Resolved { get; private set; }

        public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken)
        {
            RequestedProvider = providerKey;
            return Task.FromResult(listings);
        }

        public Task<ResolvedConnection?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
        {
            Resolved = (tenantId, connectionId);
            return Task.FromResult(resolved);
        }

        public Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubBusinessHours(IReadOnlyDictionary<Guid, BusinessHoursStatus> statuses) : IBusinessHours
    {
        public Task<BusinessHoursStatus> GetStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(statuses[connectionId]);

        public Task<IReadOnlyDictionary<Guid, BusinessHoursStatus>> GetStatusesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) => Task.FromResult(statuses);
    }
}
