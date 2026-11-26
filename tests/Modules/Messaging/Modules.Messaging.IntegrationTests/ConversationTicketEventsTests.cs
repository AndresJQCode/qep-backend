using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Soporte fase 2 (plan E11): los eventos <c>TicketOpened</c> y <c>TicketClosed</c> del hilo. Guardan sólo el id del ticket; el
/// número se resuelve al leer por <see cref="IMessagingTicketNames"/>, que acá es un doble (el adaptador real, que lee
/// <c>support.tickets</c>, se prueba en la integración de Soporte). El evento es una marca: no toca la conversación.</summary>
public sealed class ConversationTicketEventsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedTicketNames(IReadOnlyDictionary<Guid, string> numbers) : IMessagingTicketNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindNumbersAsync(
            Guid tenantId, IReadOnlyCollection<Guid> ticketIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                ticketIds.Where(numbers.ContainsKey).ToDictionary(id => id, id => numbers[id]));
    }

    private static async Task<bool> AppendAsync(
        WebApplicationFactory<Program> host, Guid tenantId, Guid conversationId, ConversationEventType type, Guid ticketId, Guid? actor,
        DateTimeOffset occurredAt)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IConversationEventAppender>()
            .AppendTicketEventAsync(tenantId, conversationId, type, ticketId, actor, occurredAt, Ct);
    }

    [Fact]
    public async Task AppendingTicketOpenedShowsInTheThreadWithTheTicketNumber()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var ticketId = Guid.CreateVersion7();
        var closedTicketId = Guid.CreateVersion7();
        var missingTicketId = Guid.CreateVersion7();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IMessagingTicketNames>(_ => new FixedTicketNames(new Dictionary<Guid, string>
            {
                [ticketId] = "SOP-000127",
                [closedTicketId] = "SOP-000128",
            }))));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(host, tenant.TenantId, connectionId, "573001234567");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        var before = await ScalarAsync<string>(connectionString,
            "SELECT version || '|' || status || '|' || unread_count || '|' || history_managed || '|' || updated_at::text FROM messaging.conversations WHERE id = @id",
            ("id", conversationId));
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-2);

        Assert.True(await AppendAsync(host, tenant.TenantId, conversationId, ConversationEventType.TicketOpened, ticketId, memberId, openedAt));
        Assert.True(await AppendAsync(host, tenant.TenantId, conversationId, ConversationEventType.TicketClosed, closedTicketId, null, openedAt.AddMinutes(1)));
        Assert.True(await AppendAsync(host, tenant.TenantId, conversationId, ConversationEventType.TicketClosed, missingTicketId, null, openedAt.AddMinutes(1.5)));

        // Una marca, no una acción del asesor: ni versión, ni estado, ni no leídos, ni history_managed.
        Assert.Equal(before, await ScalarAsync<string>(connectionString,
            "SELECT version || '|' || status || '|' || unread_count || '|' || history_managed || '|' || updated_at::text FROM messaging.conversations WHERE id = @id",
            ("id", conversationId)));
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var page = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), Ct);
        var events = page.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "Event")
            .Select(item => item.GetProperty("event"))
            .ToArray();
        Assert.Equal(3, events.Length);
        Assert.Equal("TicketOpened", events[0].GetProperty("type").GetString());
        Assert.Equal(ticketId, events[0].GetProperty("ticketId").GetGuid());
        Assert.Equal("SOP-000127", events[0].GetProperty("ticketNumber").GetString());
        Assert.Equal(memberId, events[0].GetProperty("actor").GetProperty("memberId").GetGuid());
        Assert.Equal("TicketClosed", events[1].GetProperty("type").GetString());
        Assert.Equal("SOP-000128", events[1].GetProperty("ticketNumber").GetString());
        Assert.Equal(JsonValueKind.Null, events[1].GetProperty("actor").ValueKind);
        // Un ticket que el puerto no conoce sale con su id y sin número: el hilo no falla por eso.
        Assert.Equal(missingTicketId, events[2].GetProperty("ticketId").GetGuid());
        Assert.Equal(JsonValueKind.Null, events[2].GetProperty("ticketNumber").ValueKind);
    }

    [Fact]
    public async Task AppendingToAConversationOfAnotherTenantWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var owner = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, owner.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, owner.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, owner.TenantId, connectionId, "573001234567");

        var appended = await AppendAsync(
            factory, other.TenantId, conversationId, ConversationEventType.TicketOpened, Guid.CreateVersion7(), null, DateTimeOffset.UtcNow);

        Assert.False(appended);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages"));
    }

    [Fact]
    public async Task AnyOtherEventTypeIsRefused()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");

        await Assert.ThrowsAsync<ArgumentException>(() => AppendAsync(
            factory, tenant.TenantId, conversationId, ConversationEventType.Resolved, Guid.CreateVersion7(), null, DateTimeOffset.UtcNow));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages"));
    }

    /// <summary>El tipo se guarda por nombre en <c>details</c>, pero el orden del enum es contrato con lo ya escrito: los valores
    /// viejos conservan su posición y los de Soporte van detrás de <c>AutomationContinued</c>.</summary>
    [Fact]
    public void TheEventEnumKeepsItsStoredNamesInOrder()
    {
        Assert.Equal(
            [
                "Taken", "Transferred", "Released", "AutoTaken", "Inherited", "Resolved", "Reopened", "CustomerCreated", "CustomerLinked",
                "ContactChangedNumber", "AutomationPaused", "AutomationResumed", "CustomerChanged", "ContactBlocked", "ContactUnblocked",
                "AutomationStarted", "AutomationContinued", "TicketOpened", "TicketClosed",
                "AutomationDelegated", "AutomationReturned",
            ],
            Enum.GetNames<ConversationEventType>());
        Assert.Equal(17, (int)ConversationEventType.TicketOpened);
        Assert.Equal(18, (int)ConversationEventType.TicketClosed);
    }
}
