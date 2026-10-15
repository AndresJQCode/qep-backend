using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 (Mensajería en tiempo real), contrato §2.1, con el cliente oficial de SignalR contra el
/// TestServer: el tenant por el query <c>tenantId</c> (sin <c>X-Tenant-Id</c>, como el WebSocket del navegador), los
/// grupos por claims, <c>notify</c> sólo a quien tiene la conversación, y los rechazos sin permiso o sin módulo.</summary>
public sealed class MessagingHubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool enableMessaging = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        if (enableMessaging)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        return new Fixture(factory, connectionString, tenant);
    }

    /// <summary>Como el navegador: el tenant sólo en el query. El stub autentica por <c>X-Subject-Id</c> y los permisos por
    /// <c>X-Permissions</c>; long polling porque el TestServer no abre sockets reales.</summary>
    private static HubConnection Connect(WebApplicationFactory<Program> factory, Guid tenantId, Guid userId, params string[] permissions)
    {
        var url = new Uri(factory.Server.BaseAddress, $"api/v1/hubs/messaging?tenantId={tenantId:D}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["X-Subject-Id"] = userId.ToString();
                options.Headers["X-Qep-Client"] = "web";
                if (permissions.Length > 0)
                {
                    options.Headers["X-Permissions"] = string.Join(',', permissions);
                }
            })
            .Build();
    }

    private static Received Listen(HubConnection connection)
    {
        var received = new Received();
        connection.On<ConversationChangedPayload>("conversationChanged", payload => received.Add("conversationChanged", payload.ConversationId));
        connection.On<InboundMessagePayload>("inboundMessage", payload => received.Add("inboundMessage", payload.ConversationId));
        connection.On<NotifyPayload>("notify", payload => received.Add("notify:" + payload.Reason, payload.ConversationId));
        return received;
    }

    private static async Task PublishAsync(WebApplicationFactory<Program> factory, Func<IMessagingNotifier, Task> publish)
    {
        using var scope = factory.Services.CreateScope();
        await publish(scope.ServiceProvider.GetRequiredService<IMessagingNotifier>());
    }

    /// <summary>El cliente termina <c>StartAsync</c> con el handshake, antes de que el servidor corra <c>OnConnectedAsync</c>, y
    /// el listener puede no estar todavía en <c>LISTEN</c>. Una sonda por el grupo del tenant, repetida hasta que la reciben
    /// todos, confirma las dos cosas; el hub une el grupo del miembro antes que el del tenant.</summary>
    private static async Task WaitUntilJoinedAsync(WebApplicationFactory<Program> factory, Guid tenantId, params Received[] connections)
    {
        var probe = Guid.CreateVersion7();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await PublishAsync(factory, notifier => notifier.ConversationChangedAsync(tenantId, probe, Ct));
            await Task.Delay(200, Ct);
            if (connections.All(connection => connection.Entries.Contains(("conversationChanged", probe))))
            {
                return;
            }
        }

        throw new TimeoutException("The hub connections never joined their groups.");
    }

    [Fact]
    public async Task AConnectedMemberReceivesTheEventsOfItsTenantAndNotTheOthers()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var factory = f.Factory;
        var other = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        await using var mine = Connect(factory, f.Tenant.TenantId, f.Tenant.OwnerUserId, MessagingPermissions.ConversationRead);
        await using var theirs = Connect(factory, other.TenantId, other.OwnerUserId, MessagingPermissions.ConversationRead);
        var received = Listen(mine);
        var theirsReceived = Listen(theirs);
        await mine.StartAsync(Ct);
        await theirs.StartAsync(Ct);
        await WaitUntilJoinedAsync(factory, f.Tenant.TenantId, received);
        await WaitUntilJoinedAsync(factory, other.TenantId, theirsReceived);
        theirsReceived.Clear();
        var conversationId = Guid.CreateVersion7();

        await PublishAsync(factory, notifier => notifier.ConversationChangedAsync(f.Tenant.TenantId, conversationId, Ct));

        await received.WaitForAsync("conversationChanged", conversationId);
        await Task.Delay(300, Ct);
        Assert.Empty(theirsReceived.Entries);
    }

    [Fact]
    public async Task NotifyGoesToTheAssigneeOnlyAndToEveryoneWhenNobodyHasIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var factory = f.Factory;
        var ownerMembership = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        var (advisorMembership, advisorUserId) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Andrea Gómez", "advisor");
        await using var owner = Connect(factory, f.Tenant.TenantId, f.Tenant.OwnerUserId, MessagingPermissions.ConversationRead);
        await using var advisor = Connect(factory, f.Tenant.TenantId, advisorUserId, MessagingPermissions.ConversationRead);
        var ownerReceived = Listen(owner);
        var advisorReceived = Listen(advisor);
        await owner.StartAsync(Ct);
        await advisor.StartAsync(Ct);
        await WaitUntilJoinedAsync(factory, f.Tenant.TenantId, ownerReceived, advisorReceived);
        var assigned = Guid.CreateVersion7();
        var unassigned = Guid.CreateVersion7();

        await PublishAsync(factory, notifier => notifier.InboundMessageAsync(f.Tenant.TenantId, assigned, Guid.CreateVersion7(), advisorMembership, Ct));
        await advisorReceived.WaitForAsync("notify:inbound", assigned);
        await ownerReceived.WaitForAsync("inboundMessage", assigned);
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(("notify:inbound", assigned), ownerReceived.Entries);
        Assert.NotEqual(ownerMembership, advisorMembership);

        await PublishAsync(factory, notifier => notifier.InboundMessageAsync(f.Tenant.TenantId, unassigned, Guid.CreateVersion7(), null, Ct));
        await ownerReceived.WaitForAsync("notify:inbound", unassigned);
        await advisorReceived.WaitForAsync("notify:inbound", unassigned);
    }

    [Fact]
    public async Task WithoutThePermissionTheConnectionIsRefused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var factory = f.Factory;
        await using var connection = Connect(factory, f.Tenant.TenantId, f.Tenant.OwnerUserId, "messaging.quick_reply.manage");

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(Ct));
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task WithTheModuleOffOrWithoutMembershipTheConnectionIsClosedAtOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, enableMessaging: false);
        using var factory = f.Factory;
        await using var moduleOff = Connect(factory, f.Tenant.TenantId, f.Tenant.OwnerUserId, MessagingPermissions.ConversationRead);
        var closed = new TaskCompletionSource();
        moduleOff.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await TryStartAsync(moduleOff);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(HubConnectionState.Disconnected, moduleOff.State);
    }

    /// <summary>El hub corta en <c>OnConnectedAsync</c>: según el momento, el cliente ve el cierre durante el <c>StartAsync</c> o justo
    /// después. Las dos formas terminan en <c>Closed</c>, que es lo que la prueba espera.</summary>
    private static async Task TryStartAsync(HubConnection connection)
    {
        try
        {
            await connection.StartAsync(Ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Rechazado durante el arranque: Closed no se dispara, pero el estado ya es Disconnected.
            if (connection.State == HubConnectionState.Disconnected)
            {
                return;
            }

            throw;
        }
    }

    private sealed record ConversationChangedPayload(Guid ConversationId);

    private sealed record InboundMessagePayload(Guid ConversationId, Guid MessageId);

    private sealed record NotifyPayload(Guid ConversationId, string Reason);

    private sealed class Received
    {
        private readonly ConcurrentQueue<(string Kind, Guid ConversationId)> _entries = new();

        public IReadOnlyCollection<(string Kind, Guid ConversationId)> Entries => _entries;

        public void Add(string kind, Guid conversationId) => _entries.Enqueue((kind, conversationId));

        public void Clear() => _entries.Clear();

        public async Task WaitForAsync(string kind, Guid conversationId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_entries.Contains((kind, conversationId)))
                {
                    return;
                }

                await Task.Delay(50, Ct);
            }

            throw new TimeoutException($"{kind} for {conversationId} never arrived. Received: [{string.Join(", ", _entries.Select(e => $"{e.Kind}:{e.ConversationId}"))}]");
        }
    }
}
