using BuildingBlocks.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Automations.Application;
using Modules.Messaging.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 §5.3, D-M5 y D-M14: el adaptador de Bootstrapper que le cambia la conversación de Mensajería al motor,
/// contra PostgreSQL. La conversación y sus entrantes se siembran por SQL, como en las pruebas de Mensajería; el aviso en vivo se
/// graba con un notificador falso.</summary>
public sealed class AutomationConversationsAdapterTests
{
    private const string WaId = "573001234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, string ConnectionString, Guid TenantId, Guid ConversationId, RecordingNotifier Notifier);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var notifier = new RecordingNotifier();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessagingNotifier>();
            services.AddSingleton<IMessagingNotifier>(notifier);
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, wa_id, profile_name, status, unread_count,
                last_inbound_at, last_activity_at, created_at, updated_at, version)
            VALUES (@id, @t, @c, @wa, 'Laura Gómez', 'Open', 1, now() - interval '5 minutes', now(), now(), now(), 1)
            """,
            ("id", conversationId), ("t", tenant.TenantId), ("c", connectionId), ("wa", WaId));
        return new Fixture(factory, host, connectionString, tenant.TenantId, conversationId, notifier);
    }

    private static async Task<Guid> SeedInboundAsync(Fixture f, string text, int minutesAgo)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, created_at)
            SELECT @id, c.id, c.tenant_id, c.connection_id, now() - make_interval(mins => @ago), 1, 1, 2, @text, now()
              FROM messaging.conversations AS c WHERE c.id = @c
            """,
            ("id", id), ("c", f.ConversationId), ("ago", minutesAgo), ("text", text));
        return id;
    }

    private static async Task WithAdapterAsync(Fixture f, Func<IAutomationConversations, Task> action)
    {
        using var scope = f.Host.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<IAutomationConversations>());
    }

    private static Task<string> RowAsync(Fixture f, string columns) =>
        ScalarAsync<string>(f.ConnectionString, $"SELECT {columns} FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId));

    [Fact]
    public async Task StartWritesActiveChatbotAndStep()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var chatbotId = Guid.CreateVersion7();

        await WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, chatbotId, "Bienvenida", Ct));

        Assert.Equal($"Active|{chatbotId}|Bienvenida|true|2",
            await RowAsync(f, "automation_status || '|' || automation_chatbot_id || '|' || automation_step || '|' || (automation_started_at IS NOT NULL)::text || '|' || version"));
        Assert.Equal([$"changed {f.ConversationId}"], f.Notifier.Events);
    }

    [Fact]
    public async Task StartOnAnAssignedConversationThrowsNotAvailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", Guid.CreateVersion7()));

        // El procesador la trata como skipped: el DomainException de Mensajería sale tal cual (422 si llegara a HTTP).
        var error = await Assert.ThrowsAnyAsync<DomainException>(() =>
            WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), null, Ct)));

        Assert.Equal("messaging.automation.not_available", error.Code);
        Assert.Equal("None", await RowAsync(f, "automation_status"));
        Assert.Empty(f.Notifier.Events);
    }

    [Fact]
    public async Task HandoffPausesWithASystemActorAndAnEvent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedInboundAsync(f, "primero", 10);
        var last = await SeedInboundAsync(f, "segundo", 5);
        await WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), "Menú", Ct));
        f.Notifier.Events.Clear();

        await WithAdapterAsync(f, adapter => adapter.HandoffAsync(f.TenantId, f.ConversationId, Ct));

        Assert.Equal("Paused|Handoff|true|true|true",
            await RowAsync(f, "automation_status || '|' || automation_pause_reason || '|' || (automation_paused_by_member_id IS NULL)::text || '|' || (assigned_member_id IS NULL)::text || '|' || (automation_paused_at IS NOT NULL)::text"));
        Assert.Equal("13|Handoff|false", await ScalarAsync<string>(f.ConnectionString,
            "SELECT kind || '|' || (details->>'pauseReason') || '|' || (details ? 'actor')::text FROM messaging.messages WHERE details->>'type' = 'AutomationPaused'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.automation_paused' AND actor_type = 'System' AND resource_id = @id AND tenant_id = @t",
            ("id", f.ConversationId.ToString()), ("t", f.TenantId)));
        // D-M5: suena a todo el tenant como el último entrante sin asignar.
        Assert.Equal([$"changed {f.ConversationId}", $"inbound {f.ConversationId} {last} -"], f.Notifier.Events);
    }

    [Fact]
    public async Task HandoffWithoutInboundsOnlyNotifiesTheChange()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), null, Ct));
        f.Notifier.Events.Clear();

        await WithAdapterAsync(f, adapter => adapter.HandoffAsync(f.TenantId, f.ConversationId, Ct));

        Assert.Equal("Paused", await RowAsync(f, "automation_status"));
        Assert.Equal([$"changed {f.ConversationId}"], f.Notifier.Events);
    }

    [Fact]
    public async Task TwoChangesInOneScopeSurviveAVersionBumpInBetween()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        // Una unidad del motor: arranca, el envío del bot sube la versión por SQL (MarkRepliedAsync) y el flujo pasa a un asesor.
        await WithAdapterAsync(f, async adapter =>
        {
            await adapter.StartAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), null, Ct);
            await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET version = version + 1");
            await adapter.HandoffAsync(f.TenantId, f.ConversationId, Ct);
        });

        Assert.Equal("Paused|4", await RowAsync(f, "automation_status || '|' || version"));
    }

    [Fact]
    public async Task ResolveWritesResolvedBySystem()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), "Menú", Ct));
        f.Notifier.Events.Clear();

        await WithAdapterAsync(f, adapter => adapter.ResolveAsync(f.TenantId, f.ConversationId, null, Ct));

        Assert.Equal("Resolved|None|true|true|0",
            await RowAsync(f, "status || '|' || automation_status || '|' || (automation_chatbot_id IS NULL)::text || '|' || (automation_step IS NULL)::text || '|' || unread_count"));
        Assert.Equal("false", await ScalarAsync<string>(f.ConnectionString,
            "SELECT (details ? 'actor')::text FROM messaging.messages WHERE details->>'type' = 'Resolved'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.resolved' AND actor_type = 'System' AND resource_id = @id",
            ("id", f.ConversationId.ToString())));
        Assert.Equal([$"changed {f.ConversationId}"], f.Notifier.Events);
    }

    [Fact]
    public async Task ResolveKeepsAnActiveReasonAndDropsOneThatIsNotOfTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var reasonId = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.resolution_reasons (id, tenant_id, name, created_at, updated_at, version)
            VALUES (@id, @t, 'Venta cerrada', now(), now(), 1)
            """,
            ("id", reasonId), ("t", f.TenantId));
        await WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), null, Ct));

        await WithAdapterAsync(f, adapter => adapter.ResolveAsync(f.TenantId, f.ConversationId, reasonId, Ct));

        Assert.Equal(reasonId.ToString(), await RowAsync(f, "resolution_reason_id::text"));
        Assert.Equal(reasonId.ToString(), await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'reasonId' FROM messaging.messages WHERE details->>'type' = 'Resolved'"));

        // Un chatbot importado de otra empresa puede traer un motivo que aquí no existe: se resuelve sin motivo, no se cae por la FK.
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Open', resolution_reason_id = NULL");
        await WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), null, Ct));
        await WithAdapterAsync(f, adapter => adapter.ResolveAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), Ct));

        Assert.Equal("Resolved|true", await RowAsync(f, "status || '|' || (resolution_reason_id IS NULL)::text"));
    }

    [Fact]
    public async Task EndLeavesNoneAndIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), "Menú", Ct));
        f.Notifier.Events.Clear();

        await WithAdapterAsync(f, adapter => adapter.EndAsync(f.TenantId, f.ConversationId, Ct));
        await WithAdapterAsync(f, adapter => adapter.EndAsync(f.TenantId, f.ConversationId, Ct));

        Assert.Equal("Open|None|true|true", await RowAsync(f,
            "status || '|' || automation_status || '|' || (automation_chatbot_id IS NULL)::text || '|' || (automation_step IS NULL)::text"));
        // D-M14: sin evento en el hilo; sólo el aviso del primero, el segundo no cambió nada.
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 13"));
        Assert.Equal([$"changed {f.ConversationId}"], f.Notifier.Events);
    }

    [Fact]
    public async Task EndDoesNotTouchAnAssistantThatAPersonPaused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET automation_status = 'Paused', automation_started_at = now(), automation_chatbot_id = @c, automation_paused_at = now(), automation_pause_reason = 'Manual'",
            ("c", Guid.CreateVersion7()));

        await WithAdapterAsync(f, adapter => adapter.EndAsync(f.TenantId, f.ConversationId, Ct));

        Assert.Equal("Paused|Manual", await RowAsync(f, "automation_status || '|' || automation_pause_reason"));
        Assert.Empty(f.Notifier.Events);
    }

    [Fact]
    public async Task SetStepWritesTheStepWithoutMovingTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        // Sin asistente no hay paso que guardar (D-M11).
        await WithAdapterAsync(f, adapter => adapter.SetStepAsync(f.TenantId, f.ConversationId, "Huérfano", Ct));
        Assert.Equal("true", await RowAsync(f, "(automation_step IS NULL)::text"));
        Assert.Empty(f.Notifier.Events);

        await WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, Guid.CreateVersion7(), null, Ct));
        f.Notifier.Events.Clear();
        await WithAdapterAsync(f, adapter => adapter.SetStepAsync(f.TenantId, f.ConversationId, "  " + new string('a', 130) + "  ", Ct));

        // La columna es de 120 y el título de un nodo puede pasarse; la versión no se mueve (un entrante en el medio no la vuelve 412).
        Assert.Equal($"{new string('a', 120)}|2", await RowAsync(f, "automation_step || '|' || version"));
        Assert.Equal([$"changed {f.ConversationId}"], f.Notifier.Events);
    }

    [Fact]
    public async Task GetReadsTheStateTheEngineNeeds()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var chatbotId = Guid.CreateVersion7();
        await WithAdapterAsync(f, adapter => adapter.StartAsync(f.TenantId, f.ConversationId, chatbotId, null, Ct));
        var connectionId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT connection_id FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId));
        var lastInboundAt = await ScalarAsync<DateTime>(f.ConnectionString, "SELECT last_inbound_at FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId));

        AutomationConversationState? state = null;
        AutomationConversationState? foreign = null;
        await WithAdapterAsync(f, async adapter =>
        {
            state = await adapter.GetAsync(f.TenantId, f.ConversationId, Ct);
            foreign = await adapter.GetAsync(Guid.CreateVersion7(), f.ConversationId, Ct);
        });

        Assert.NotNull(state);
        Assert.Equal(
            new AutomationConversationState("Open", null, "Active", chatbotId, connectionId, new DateTimeOffset(lastInboundAt, TimeSpan.Zero), "Laura Gómez", WaId),
            state with { LastInboundAt = state.LastInboundAt?.ToUniversalTime() });
        Assert.Null(foreign);
    }

    private sealed class RecordingNotifier : IMessagingNotifier
    {
        public List<string> Events { get; } = [];

        public Task ConversationChangedAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add($"changed {conversationId}");
            }

            return Task.CompletedTask;
        }

        public Task InboundMessageAsync(Guid tenantId, Guid conversationId, Guid messageId, Guid? assignedMemberId, CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add($"inbound {conversationId} {messageId} {assignedMemberId?.ToString() ?? "-"}");
            }

            return Task.CompletedTask;
        }
    }
}
