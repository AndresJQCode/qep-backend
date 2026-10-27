using System.Net;
using Npgsql;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 §4.1 y §4.2 contra Postgres: el esquema <c>automations</c> con sus siete tablas, las dos unicidades
/// parciales que el motor y el constructor dan por hechas (D-C4, D-M12) y el grupo de rutas del módulo.</summary>
public sealed class AutomationsSchemaTests
{
    private static readonly string[] ExpectedTables =
    [
        "__ef_migrations_history", "chatbot_connections", "chatbot_templates", "chatbot_versions", "chatbots", "conversation_runs",
        "http_calls", "run_units", "simulations",
    ];

    private static readonly string[] ExpectedChecks =
        [
            "CK_chatbots_exhausted_action", "CK_chatbots_exhausted_member", "CK_chatbots_max_attempts", "CK_chatbots_published",
            "CK_chatbots_status", "CK_chatbots_trigger",
        ];

    private static readonly string[] ExpectedIndexes =
    [
        "IX_chatbots_tenant_name", "IX_chatbot_connections_active", "IX_chatbot_templates_tenant_name", "IX_conversation_runs_live",
        "IX_conversation_runs_tenant_chatbot_started", "IX_http_calls_tenant_called",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSchemaHasTheSevenTables()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();

        var tables = await ListAsync(connectionString,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'automations' AND table_type = 'BASE TABLE'");

        Assert.Equal(
            ExpectedTables,
            tables.Order(StringComparer.Ordinal));
    }

    // §4.2: los nombres que la unidad de trabajo traduce (y los del motor) tienen que existir tal cual.
    [Fact]
    public async Task TheChecksAndIndexesHaveTheSpecNames()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();

        var checks = await ListAsync(connectionString,
            "SELECT conname FROM pg_constraint WHERE contype = 'c' AND connamespace = 'automations'::regnamespace");
        var indexes = await ListAsync(connectionString, "SELECT indexname FROM pg_indexes WHERE schemaname = 'automations'");

        Assert.Equal(
            ExpectedChecks,
            checks.Order(StringComparer.Ordinal));
        foreach (var name in ExpectedIndexes)
        {
            Assert.Contains(name, indexes);
        }

        var nameIndex = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'automations' AND indexname = 'IX_chatbots_tenant_name'");
        Assert.Contains("UNIQUE", nameIndex, StringComparison.Ordinal);
        Assert.Contains("lower((name)::text)", nameIndex, StringComparison.Ordinal);
    }

    /// <summary>Ruling P2-R44: las dos lecturas del motor que corren sin la corrida viva (la última corrida de la conversación en cada
    /// unidad sin corrida, P2-R24, y el barrido de abandonadas, D-M13) tienen su índice; sin ellos son un seq scan de conversation_runs.</summary>
    [Fact]
    public async Task TheEngineReadsHaveTheirIndexes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();

        var latest = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'automations' AND indexname = 'IX_conversation_runs_tenant_conversation_started'");
        var waiting = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'automations' AND indexname = 'IX_conversation_runs_waiting_updated'");

        Assert.EndsWith("(tenant_id, conversation_id, started_at DESC)", latest, StringComparison.Ordinal);
        Assert.Contains("(updated_at) WHERE ((status)::text = 'Waiting'::text)", waiting, StringComparison.Ordinal);
    }

    // Spec 2026-10-25 §6.1: run_units con su unicidad por entrante (parcial) y el índice de lectura por corrida.
    [Fact]
    public async Task RunUnitsHasItsTwoIndexes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();

        var inbound = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'automations' AND indexname = 'IX_run_units_run_inbound'");
        var byRun = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'automations' AND indexname = 'IX_run_units_tenant_run'");

        Assert.Contains("UNIQUE", inbound, StringComparison.Ordinal);
        Assert.Contains("(run_id, inbound_message_id) WHERE (inbound_message_id IS NOT NULL)", inbound, StringComparison.Ordinal);
        Assert.EndsWith("(tenant_id, run_id, base_step)", byRun, StringComparison.Ordinal);
    }

    // Spec 2026-10-25 D-2: la regla de agotamiento sólo admite los dos valores y sólo Handoff lleva miembro.
    [Fact]
    public async Task TheExhaustedRuleRejectsAnUnknownActionAndAMemberWithRestart()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var id = await InsertChatbotAsync(connectionString, Guid.CreateVersion7(), "Ventas");

        var unknownAction = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE automations.chatbots SET exhausted_action = 'Other' WHERE id = @id", ("id", id)));
        var memberWithRestart = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE automations.chatbots SET exhausted_action = 'Restart', exhausted_member_id = @member WHERE id = @id",
            ("id", id), ("member", Guid.CreateVersion7())));

        Assert.Equal(PostgresErrorCodes.CheckViolation, unknownAction.SqlState);
        Assert.Equal("CK_chatbots_exhausted_action", unknownAction.ConstraintName);
        Assert.Equal(PostgresErrorCodes.CheckViolation, memberWithRestart.SqlState);
        Assert.Equal("CK_chatbots_exhausted_member", memberWithRestart.ConstraintName);
        // Handoff con o sin miembro es válido.
        await ExecuteAsync(connectionString,
            "UPDATE automations.chatbots SET exhausted_action = 'Handoff', exhausted_member_id = @member WHERE id = @id",
            ("id", id), ("member", Guid.CreateVersion7()));
        await ExecuteAsync(connectionString,
            "UPDATE automations.chatbots SET exhausted_action = 'Handoff', exhausted_member_id = NULL WHERE id = @id", ("id", id));
    }

    // D-C4: una conexión, un chatbot activo.
    [Fact]
    public async Task OnlyOneActiveChatbotPerConnection()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var first = await InsertChatbotAsync(connectionString, tenantId, "Ventas");
        var second = await InsertChatbotAsync(connectionString, tenantId, "Soporte");
        await InsertChatbotConnectionAsync(connectionString, tenantId, first, connectionId, active: true);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertChatbotConnectionAsync(connectionString, tenantId, second, connectionId, active: true));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("IX_chatbot_connections_active", error.ConstraintName);
        // Inactiva no compite: el borrador puede apuntar a la misma conexión.
        await InsertChatbotConnectionAsync(connectionString, tenantId, second, connectionId, active: false);
    }

    // D-M12: una corrida viva por conversación.
    [Fact]
    public async Task OnlyOneLiveRunPerConversation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await InsertRunAsync(connectionString, tenantId, conversationId, "Waiting");

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(connectionString, tenantId, conversationId, "Waiting"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("IX_conversation_runs_live", error.ConstraintName);
        // Las terminadas no cuentan: el historial de una conversación guarda varias.
        await InsertRunAsync(connectionString, tenantId, conversationId, "Completed");
        await InsertRunAsync(connectionString, tenantId, conversationId, "HandedOff");
    }

    // La revisión de la tarea 9: una ruta desconocida da 404 aun con el grupo vacío, así que no prueba nada. Esto sí: el grupo
    // existe y sus rutas pasan por TenantModuleGuard (sin automations, 403 con código), y lo que no está mapeado sigue en 404.
    [Fact]
    public async Task TheChatbotsRouteWithoutTheModuleIs403AndAnUnknownRouteIs404()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var withoutModule = await client.GetAsync($"{AutomationsUrl(tenant.TenantId)}/chatbots", Ct);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var unknown = await client.GetAsync($"{AutomationsUrl(tenant.TenantId)}/nothing", Ct);
        var withModule = await client.GetAsync($"{AutomationsUrl(tenant.TenantId)}/chatbots", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, withoutModule.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(withoutModule)).Code);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withModule.StatusCode);
    }

    private static async Task<Guid> InsertChatbotAsync(string connectionString, Guid tenantId, string name)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO automations.chatbots (id, tenant_id, name, draft_flow, draft_updated_at, draft_updated_by, created_at, created_by, updated_at)
            VALUES (@id, @tenantId, @name, '{}'::jsonb, now(), @by, now(), @by, now())
            """,
            ("id", id), ("tenantId", tenantId), ("name", name), ("by", Guid.CreateVersion7()));
        return id;
    }

    private static Task<int> InsertChatbotConnectionAsync(string connectionString, Guid tenantId, Guid chatbotId, Guid connectionId, bool active) =>
        ExecuteAsync(connectionString,
            """
            INSERT INTO automations.chatbot_connections (chatbot_id, tenant_id, connection_id, active)
            VALUES (@chatbotId, @tenantId, @connectionId, @active)
            """,
            ("chatbotId", chatbotId), ("tenantId", tenantId), ("connectionId", connectionId), ("active", active));

    private static Task<int> InsertRunAsync(string connectionString, Guid tenantId, Guid conversationId, string status) =>
        ExecuteAsync(connectionString,
            """
            INSERT INTO automations.conversation_runs (id, tenant_id, conversation_id, connection_id, chatbot_id, chatbot_version_id, status, started_at, updated_at)
            VALUES (@id, @tenantId, @conversationId, @connectionId, @chatbotId, @versionId, @status, now(), now())
            """,
            ("id", Guid.CreateVersion7()), ("tenantId", tenantId), ("conversationId", conversationId), ("connectionId", Guid.CreateVersion7()),
            ("chatbotId", Guid.CreateVersion7()), ("versionId", Guid.CreateVersion7()), ("status", status));

    private static async Task<List<string>> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
