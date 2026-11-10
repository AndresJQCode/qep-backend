using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-09 «Auditoría» y prueba 10: una fila de <c>audit.entries</c> por acción, con el usuario como actor (<c>Human</c>) o
/// el sistema (<c>System</c>, actor vacío) cuando la hace el worker, y ninguna cuando el 409 de la carrera del <c>clientId</c> deshace la
/// escritura: la entrada de <c>.created</c> se agrega al tracker antes del INSERT y no puede salir con un request que no guardó.</summary>
public sealed class ScheduledMessageAuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Created = MessagingAuditActions.ScheduledMessageCreated;

    private static Task<long> HumanEntriesAsync(ScheduledFixture f, string action, Guid id) =>
        CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_type = 'scheduled_message' AND resource_id = @id AND actor_type = 'Human' AND actor_id = @actor AND tenant_id = @tenant",
            ("action", action), ("id", id.ToString()), ("actor", f.Tenant.OwnerUserId), ("tenant", f.Tenant.TenantId));

    private static Task<long> AllCreatedAsync(ScheduledFixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = @action", ("action", Created));

    [Fact]
    public async Task EveryHumanActionLeavesItsEntryWithTheActor()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var first = await f.CreateTextAsync();
        var a = first.GetProperty("id").GetGuid();
        Assert.Equal(1, await HumanEntriesAsync(f, Created, a));

        // Editar: la hora cambia, y es lo único que el entry anota.
        var edit = await f.EditAsync(a, new { expectedVersion = 1, scheduledAt = ScheduledFixture.InTwoHours().AddMinutes(30) });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(1, await HumanEntriesAsync(f, MessagingAuditActions.ScheduledMessageUpdated, a));
        Assert.Equal("[\"scheduledAt\"]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = @action AND resource_id = @id", ("action", MessagingAuditActions.ScheduledMessageUpdated), ("id", a.ToString())));

        // Una edición que no cambia nada responde 200 y no deja entry.
        var noop = await f.EditAsync(a, new { expectedVersion = 2, text = "Te escribo como quedamos" });
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.Equal(1, await f.AuditCountAsync(MessagingAuditActions.ScheduledMessageUpdated, a));

        Assert.Equal(HttpStatusCode.Accepted, (await f.ActAsync(a, "send-now", 2)).StatusCode);
        Assert.Equal(1, await HumanEntriesAsync(f, MessagingAuditActions.ScheduledMessageSendNow, a));

        Assert.Equal(HttpStatusCode.NoContent, (await f.ActAsync(a, "cancel", 3)).StatusCode);
        Assert.Equal(1, await HumanEntriesAsync(f, MessagingAuditActions.ScheduledMessageCancelled, a));

        var second = await f.CreateTextAsync();
        var b = second.GetProperty("id").GetGuid();
        await f.MakeFailedAsync(b);
        Assert.Equal(HttpStatusCode.Accepted, (await f.ActAsync(b, "retry", await f.VersionAsync())).StatusCode);
        Assert.Equal(1, await HumanEntriesAsync(f, MessagingAuditActions.ScheduledMessageRetried, b));

        await f.MakeFailedAsync(b);
        Assert.Equal(HttpStatusCode.NoContent, (await f.ActAsync(b, "discard", await f.VersionAsync())).StatusCode);
        Assert.Equal(1, await HumanEntriesAsync(f, MessagingAuditActions.ScheduledMessageDiscarded, b));

        // La nota de voz (el otro camino de crear) también audita su creación.
        var voice = await f.PostVoiceNoteAsync(Guid.CreateVersion7(), ScheduledFixture.InTwoHours());
        Assert.Equal(HttpStatusCode.Created, voice.StatusCode);
        Assert.Equal(1, await HumanEntriesAsync(f, Created, (await voice.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid()));
        Assert.Equal(3, await AllCreatedAsync(f));
    }

    [Fact]
    public async Task TheWorkerLeavesSentAndFailedAsSystem()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var sent = (await f.CreateTextAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await f.ActAsync(sent, "send-now", 1)).StatusCode);
        await DrainScheduledMessagesAsync(f.Host);

        var failed = (await f.CreateTextAsync()).GetProperty("id").GetGuid();
        // La ventana se cierra después de programar: el ejecutor la vuelve a comprobar al enviar.
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");
        Assert.Equal(HttpStatusCode.Accepted, (await f.ActAsync(failed, "send-now", 1)).StatusCode);
        await DrainScheduledMessagesAsync(f.Host);

        Assert.Equal(1, f.MetaSends());
        Assert.Equal(1, await SystemEntriesAsync(f, MessagingAuditActions.ScheduledMessageSent, sent));
        Assert.Equal(1, await SystemEntriesAsync(f, MessagingAuditActions.ScheduledMessageFailed, failed));
        Assert.Equal(0, await f.AuditCountAsync(MessagingAuditActions.ScheduledMessageFailed, sent));
        Assert.Equal(0, await f.AuditCountAsync(MessagingAuditActions.ScheduledMessageSent, failed));
    }

    private static Task<long> SystemEntriesAsync(ScheduledFixture f, string action, Guid id) =>
        CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_type = 'scheduled_message' AND resource_id = @id AND actor_type = 'System' AND actor_id = @empty",
            ("action", action), ("id", id.ToString()), ("empty", Guid.Empty));

    /// <summary>Un repositorio que, a pedido, no ve lo que otro request acaba de guardar: así el POST llega al INSERT que el índice rechaza,
    /// que es lo que pasa en la carrera real (los dos requests miraron antes de que cualquiera guardara).</summary>
    private sealed class RaceGate
    {
        public volatile bool HideActive;

        public int SkipClientLookups;
    }

    private sealed class RacingRepository(IScheduledMessageRepository inner, RaceGate gate) : IScheduledMessageRepository
    {
        public Task<ScheduledMessage?> FindByClientIdAsync(Guid tenantId, Guid conversationId, Guid clientId, CancellationToken cancellationToken) =>
            Interlocked.Decrement(ref gate.SkipClientLookups) >= 0
                ? Task.FromResult<ScheduledMessage?>(null)
                : inner.FindByClientIdAsync(tenantId, conversationId, clientId, cancellationToken);

        public Task<ScheduledMessage?> FindActiveAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            gate.HideActive ? Task.FromResult<ScheduledMessage?>(null) : inner.FindActiveAsync(tenantId, conversationId, cancellationToken);

        public Task<IScheduledMessageLock?> LockAsync(Guid tenantId, Guid conversationId, Guid scheduledMessageId, CancellationToken cancellationToken) =>
            inner.LockAsync(tenantId, conversationId, scheduledMessageId, cancellationToken);

        public Task AddAsync(ScheduledMessage message, CancellationToken cancellationToken) => inner.AddAsync(message, cancellationToken);
    }

    private static Task<ScheduledFixture> ArrangeRacingAsync(TestDatabase database, RaceGate gate) =>
        ScheduledFixture.ArrangeAsync(database, services =>
            services.AddScoped<IScheduledMessageRepository>(provider =>
                new RacingRepository(ActivatorUtilities.CreateInstance<ScheduledMessageRepository>(provider), gate)));

    [Fact]
    public async Task ACreateThatLosesTheRaceWith409WritesNoCreatedAudit()
    {
        await using var database = await StartDatabaseAsync();
        var gate = new RaceGate();
        using var f = await ArrangeRacingAsync(database, gate);
        await f.CreateTextAsync();
        Assert.Equal(1, await AllCreatedAsync(f));
        gate.HideActive = true;

        var loser = await f.PostTextAsync(Guid.CreateVersion7(), ScheduledFixture.InTwoHours());

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("messaging.scheduled.already_exists", (await ProblemAsync(loser)).Code);
        // Nada del request condenado llegó a la base, ni la fila ni su entrada de auditoría, ni con requests posteriores.
        gate.HideActive = false;
        await f.Client.GetAsync(f.ScheduledUrl, Ct);
        Assert.Equal(1, await AllCreatedAsync(f));
        Assert.Equal(1, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.scheduled_messages WHERE conversation_id = @c", ("c", f.ConversationId)));
    }

    [Fact]
    public async Task ACreateThatLosesTheSameClientIdRaceAnswersTheWinnerWithoutASecondCreatedAudit()
    {
        await using var database = await StartDatabaseAsync();
        var gate = new RaceGate();
        using var f = await ArrangeRacingAsync(database, gate);
        var clientId = Guid.CreateVersion7();
        var winner = await f.CreateTextAsync(clientId);
        gate.HideActive = true;
        gate.SkipClientLookups = 1;

        var repeated = await f.PostTextAsync(clientId, ScheduledFixture.InTwoHours());

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(winner.GetProperty("id").GetGuid(), (await repeated.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        gate.HideActive = false;
        await f.Client.GetAsync(f.ScheduledUrl, Ct);
        Assert.Equal(1, await AllCreatedAsync(f));
    }
}
