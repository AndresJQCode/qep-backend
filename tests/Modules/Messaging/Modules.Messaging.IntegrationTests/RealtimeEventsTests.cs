using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.ListenerReadiness;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 (Mensajería en tiempo real), decisiones 2 y 6: lo que cambia se publica por
/// <c>pg_notify</c> después del commit, el listener de cada pod lo entrega a sus sinks, y el sobre lleva sólo
/// ids. Se prueba contra la base real: el camino entre pods es Postgres, no memoria.</summary>
public sealed class RealtimeEventsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary><c>Host</c> es el derivado con el sink; <c>Factory</c> es el padre, que se dispone al final y arrastra al derivado.</summary>
    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, string ConnectionString, RegisteredTenant Tenant, RecordingEventSink Sink, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var sink = new RecordingEventSink();
        var factory = new QepApiFactory(connectionString);
        var host = factory.WithEventSink(sink);
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        await WaitUntilListeningAsync(host, sink);
        return new Fixture(factory, host, connectionString, tenant, sink, CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    [Fact]
    public async Task APublishedEventTravelsThroughPostgresToTheSink()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var sink = new RecordingEventSink();
        using var factory = new QepApiFactory(connectionString);
        using var host = factory.WithEventSink(sink);
        await WaitUntilListeningAsync(host, sink);
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();

        using (var scope = host.Services.CreateScope())
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IMessagingNotifier>();
            await notifier.ConversationChangedAsync(tenantId, conversationId, Ct);
            await notifier.InboundMessageAsync(tenantId, conversationId, messageId, memberId, Ct);
        }

        var changed = await sink.WaitForAsync(e => e.Kind == MessagingEventKinds.ConversationChanged && e.ConversationId == conversationId);
        var inbound = await sink.WaitForAsync(e => e.Kind == MessagingEventKinds.InboundMessage && e.ConversationId == conversationId);
        Assert.Equal(new MessagingEvent(MessagingEventKinds.ConversationChanged, tenantId, conversationId, null, null), changed);
        Assert.Equal(new MessagingEvent(MessagingEventKinds.InboundMessage, tenantId, conversationId, messageId, memberId), inbound);
    }

    [Fact]
    public async Task AnInboundNotifiesWithTheAssigneeAndTheWritesNotifyTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var host = f.Host;
        using var anonymous = host.CreateClient();

        // 1. Un entrante en una conversación nueva, sin asignado: a todo el tenant.
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in.1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(host);
        var first = await f.Sink.WaitForAsync(e => e.Kind == MessagingEventKinds.InboundMessage);
        Assert.Equal((f.Tenant.TenantId, (Guid?)null), (first.TenantId, first.AssignedMemberId));
        Assert.NotNull(first.MessageId);

        // 2. El dueño la toma: conversationChanged; el siguiente entrante le suena a él.
        var item = (await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct)).GetProperty("items")[0];
        var conversationId = item.GetProperty("id").GetGuid();
        var url = ConversationUrl(f.Tenant.TenantId, conversationId);
        f.Sink.Clear();
        var taken = await SendAsync(f.Client, HttpMethod.Post, $"{url}/take", ifMatch: $"\"{item.GetProperty("version").GetInt64()}\"");
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        var changedByTake = await f.Sink.WaitForAsync(e => e.Kind == MessagingEventKinds.ConversationChanged);
        Assert.Equal(conversationId, changedByTake.ConversationId);

        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30, "¿sigues ahí?"));
        await DrainDeliveriesAsync(host);
        var second = await f.Sink.WaitForAsync(e => e.Kind == MessagingEventKinds.InboundMessage);
        Assert.Equal(await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant), second.AssignedMemberId);

        // 3. Leer y resolver también avisan; un reenvío de Meta (mismo wamid) no avisa nada.
        f.Sink.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/read")).StatusCode);
        await f.Sink.WaitForAsync(e => e.Kind == MessagingEventKinds.ConversationChanged);
        f.Sink.Clear();
        var current = await f.Client.GetFromJsonAsync<JsonElement>(url, Ct);
        var resolved = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{current.GetProperty("version").GetInt64()}\"");
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        await f.Sink.WaitForAsync(e => e.Kind == MessagingEventKinds.ConversationChanged);

        f.Sink.Clear();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30, "¿sigues ahí?"));
        await DrainDeliveriesAsync(host);
        await Task.Delay(500, Ct);
        Assert.Empty(f.Sink.Events);
    }

    /// <summary>Spec 2026-10-14 (Instagram) sobre el tiempo real: la bandeja es una sola, así que un DM suena igual que un
    /// mensaje de WhatsApp, y un echo de la app —que mueve la foto de la lista sin ser un entrante— avisa que la conversación
    /// cambió, no que llegó algo. Un reenvío del mismo <c>mid</c> no avisa nada.</summary>
    [Fact]
    public async Task AnInstagramDmRingsAndAnEchoFromTheAppOnlyChangesTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var sink = new RecordingEventSink();
        using var factory = new QepApiFactory(connectionString);
        using var host = factory.WithEventSink(sink);
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedInstagramConnectionAsync(host, tenant.TenantId, "Instagram Ventas", InstagramPayloads.DefaultAccountId);
        factory.MetaHandler.Respond($@"^/v24\.0/{InstagramPayloads.DefaultUserId}\?", HttpStatusCode.OK, """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        await WaitUntilListeningAsync(host, sink);
        using var anonymous = host.CreateClient();

        // 1. Un DM en una conversación nueva, sin asignado: inboundMessage a todo el tenant, con el id del mensaje.
        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Text("m1", 1760000000, "hola"))).StatusCode);
        await DrainDeliveriesAsync(host);
        var inbound = await sink.WaitForAsync(e => e.Kind == MessagingEventKinds.InboundMessage);
        Assert.Equal((tenant.TenantId, (Guid?)null), (inbound.TenantId, inbound.AssignedMemberId));
        Assert.NotNull(inbound.MessageId);

        // 2. Un echo desde la app de Instagram: conversationChanged de esa misma conversación, y ningún inboundMessage.
        sink.Clear();
        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Echo("m_app", 1760000200, "Respondido desde el celular"))).StatusCode);
        await DrainDeliveriesAsync(host);
        var changed = await sink.WaitForAsync(e => e.Kind == MessagingEventKinds.ConversationChanged);
        Assert.Equal(inbound.ConversationId, changed.ConversationId);
        Assert.DoesNotContain(sink.Events, e => e.Kind == MessagingEventKinds.InboundMessage);

        // 3. Meta reenvía el mismo echo: ya estaba, no avisa nada.
        sink.Clear();
        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Echo("m_app", 1760000200, "Respondido desde el celular"))).StatusCode);
        await DrainDeliveriesAsync(host);
        await Task.Delay(500, Ct);
        Assert.Empty(sink.Events);

        // 4. El visto marca leído un saliente nuestro: conversationChanged, como un acuse de WhatsApp. Uno que no cambia nada, no avisa.
        await SeedOutboundAsync(connectionString, inbound.ConversationId, tenant.TenantId, connectionId, "m_out", occurredAtUnix: 1760000300);
        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Seen("m_out", 1760000310))).StatusCode);
        await DrainDeliveriesAsync(host);
        var seen = await sink.WaitForAsync(e => e.Kind == MessagingEventKinds.ConversationChanged);
        Assert.Equal(inbound.ConversationId, seen.ConversationId);
        sink.Clear();
        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Seen("m_out", 1760000310))).StatusCode);
        await DrainDeliveriesAsync(host);
        await Task.Delay(500, Ct);
        Assert.Empty(sink.Events);

        // 5. La persona borró su mensaje: la burbuja cambia, conversationChanged.
        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Deleted("m1", 1760000400))).StatusCode);
        await DrainDeliveriesAsync(host);
        var deleted = await sink.WaitForAsync(e => e.Kind == MessagingEventKinds.ConversationChanged);
        Assert.Equal(inbound.ConversationId, deleted.ConversationId);
    }
}

internal static class ListenerReadiness
{
    /// <summary>El listener entra en <c>LISTEN</c> poco después de arrancar el host; un aviso publicado antes se pierde (en producción
    /// lo cubre el polling). Una sonda repetida hasta que llega, y la cola vacía para la prueba.</summary>
    public static async Task WaitUntilListeningAsync(WebApplicationFactory<Program> host, RecordingEventSink sink)
    {
        var probe = Guid.CreateVersion7();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using (var scope = host.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMessagingNotifier>()
                    .ConversationChangedAsync(Guid.CreateVersion7(), probe, TestContext.Current.CancellationToken);
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
            if (sink.Events.Any(e => e.ConversationId == probe))
            {
                sink.Clear();
                return;
            }
        }

        throw new TimeoutException("The messaging events listener never started listening.");
    }
}

/// <summary>Un sink que acumula lo que el listener le entrega, para que la prueba espere lo que publicó.</summary>
internal sealed class RecordingEventSink : IMessagingEventSink
{
    private readonly ConcurrentQueue<MessagingEvent> _events = new();

    public IReadOnlyCollection<MessagingEvent> Events => _events;

    public Task DispatchAsync(MessagingEvent notification, CancellationToken cancellationToken)
    {
        _events.Enqueue(notification);
        return Task.CompletedTask;
    }

    public void Clear() => _events.Clear();

    /// <summary>Hasta 10 s: el <c>NOTIFY</c> llega en milisegundos; el margen es para una máquina de CI lenta.</summary>
    public async Task<MessagingEvent> WaitForAsync(Func<MessagingEvent, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = _events.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No event matched. Received: [{string.Join(", ", _events.Select(e => $"{e.Kind}:{e.ConversationId}"))}]");
    }
}

internal static class RealtimeHarness
{
    /// <summary>Un sink más, al lado del del hub: el listener entrega a todos los <c>IMessagingEventSink</c> registrados.
    /// Como <c>WithTestClock</c>, va por <c>ConfigureTestServices</c>, que corre después del registro del host.</summary>
    public static WebApplicationFactory<Program> WithEventSink(this WebApplicationFactory<Program> factory, RecordingEventSink sink) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingEventSink>(sink)));
}
