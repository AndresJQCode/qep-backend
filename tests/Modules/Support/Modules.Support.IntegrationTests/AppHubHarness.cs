using System.Collections.Concurrent;
using System.Text.Json;
using BuildingBlocks.Application.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// The official SignalR client against the TestServer for the global hub (spec 2026-11-15 §5.1, plan N1): the tenant only in the
/// <c>tenantId</c> query, like the browser's WebSocket; long polling because the TestServer does not open real sockets.
/// </summary>
internal static class AppHubHarness
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static HubConnection Connect(QepApiFactory factory, string tenantId, string subjectId)
    {
        var url = new Uri(factory.Server.BaseAddress, $"api/v1/hubs/app?tenantId={tenantId}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["X-Subject-Id"] = subjectId;
                options.Headers["X-Qep-Client"] = "web";
            })
            .Build();
    }

    /// <summary>A tenant with a member of <paramref name="role"/> and that member's connection, already started. A
    /// <paramref name="tenant"/> passed in is the caller's, already seeded with its own modules: seeding it again would turn every
    /// module back on (SeedTenantAsync inserts all of them by default).</summary>
    public static async Task<(string Tenant, Guid MemberId, HubConnection Connection, Received Received)> ConnectMemberAsync(
        QepApiFactory factory, string role = "advisor", string displayName = "Ana", string? tenant = null)
    {
        if (tenant is null)
        {
            tenant = NewTenantId();
            await SeedTenantAsync(factory, tenant);
        }

        var subject = Guid.CreateVersion7().ToString();
        var member = await SeedCallerMembershipAsync(factory, displayName, role, tenant, subject);
        var connection = Connect(factory, tenant, subject);
        var received = Listen(connection);
        await connection.StartAsync(Ct);
        return (tenant, member, connection, received);
    }

    public static Received Listen(HubConnection connection)
    {
        var received = new Received();
        connection.On<JsonElement>(SupportRealtimeEvents.TicketChanged, payload => received.Add(SupportRealtimeEvents.TicketChanged, payload));
        connection.On<JsonElement>("presenceChanged", payload => received.Add("presenceChanged", payload));
        return received;
    }

    public static async Task PublishTicketChangedAsync(QepApiFactory factory, Guid tenantId, string topic, Guid ticketId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IRealtimePublisher>().PublishAsync(
            tenantId, topic, SupportRealtimeEvents.TicketChanged, new { ticketId }, Ct);
    }

    /// <summary>
    /// The listener may not be in <c>LISTEN</c> yet right after the host starts: a probe published until it arrives confirms the whole
    /// path (publisher, NOTIFY, listener, group) before the test asserts anything that depends on it. Earlier copies of the probe can
    /// still be in flight when this returns, so a test asserts on the ticket it publishes afterwards, never on an empty list.
    /// </summary>
    public static async Task WaitUntilDeliveredAsync(QepApiFactory factory, Guid tenantId, string topic, Received received)
    {
        var probe = Guid.CreateVersion7();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await PublishTicketChangedAsync(factory, tenantId, topic, probe);
            await Task.Delay(200, Ct);
            if (received.HasTicket(probe))
            {
                return;
            }
        }

        throw new TimeoutException("The realtime probe never arrived.");
    }

    /// <summary>The hub aborts in <c>OnConnectedAsync</c>: depending on timing the client sees the close during <c>StartAsync</c> or
    /// right after. Both end disconnected, which is what the test expects.</summary>
    public static async Task TryStartAsync(HubConnection connection)
    {
        try
        {
            await connection.StartAsync(Ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (connection.State == HubConnectionState.Disconnected)
            {
                return;
            }

            throw;
        }
    }

    internal sealed class Received
    {
        private readonly ConcurrentQueue<(string Event, JsonElement Payload)> _entries = new();

        public IReadOnlyList<(string Event, JsonElement Payload)> Entries => [.. _entries];

        public void Add(string @event, JsonElement payload) => _entries.Enqueue((@event, payload.Clone()));

        public void Clear() => _entries.Clear();

        public bool HasTicket(Guid ticketId) => _entries.Any(entry =>
            entry.Event == SupportRealtimeEvents.TicketChanged
            && entry.Payload.TryGetProperty("ticketId", out var id)
            && id.GetGuid() == ticketId);

        public async Task WaitForTicketAsync(Guid ticketId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (HasTicket(ticketId))
                {
                    return;
                }

                await Task.Delay(50, Ct);
            }

            throw new TimeoutException($"supportTicketChanged for {ticketId} never arrived.");
        }

        /// <summary>How many entries arrived so far: a mark to read only what arrives after it (<see cref="PresenceAfter"/>).</summary>
        public int Count => _entries.Count;

        /// <summary>The <c>presenceChanged</c> payloads received after <paramref name="mark"/> (a <see cref="Count"/> taken
        /// earlier), in arrival order.</summary>
        public IReadOnlyList<JsonElement> PresenceAfter(int mark) =>
            [.. Entries.Skip(mark).Where(entry => entry.Event == "presenceChanged").Select(entry => entry.Payload)];

        /// <summary>The last <c>presenceChanged</c> received that matches <paramref name="match"/>, waiting up to 10 s.</summary>
        public Task<JsonElement> WaitForPresenceAsync(Func<JsonElement, bool> match) => WaitForPresenceAsync(match, times: 1);

        /// <summary>Waits until <paramref name="times"/> <c>presenceChanged</c> matching <paramref name="match"/> arrived (up to
        /// 10 s) and returns the last one; with one entry per announcement it drains what earlier actions still had in flight.</summary>
        public async Task<JsonElement> WaitForPresenceAsync(Func<JsonElement, bool> match, int times)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var found = _entries.Where(entry => entry.Event == "presenceChanged" && match(entry.Payload)).ToList();
                if (found.Count >= times)
                {
                    return found[^1].Payload;
                }

                await Task.Delay(50, Ct);
            }

            throw new TimeoutException("The expected presenceChanged never arrived.");
        }

        /// <summary>The first <c>presenceChanged</c> received after <paramref name="mark"/>, waiting up to 10 s.</summary>
        public async Task<JsonElement> WaitForPresenceAfterAsync(int mark)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var after = PresenceAfter(mark);
                if (after.Count > 0)
                {
                    return after[0];
                }

                await Task.Delay(50, Ct);
            }

            throw new TimeoutException("No presenceChanged arrived after the mark.");
        }
    }
}
