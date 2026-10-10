using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §7: lo que sólo la base hace cumplir —CHECKs, únicos parciales, la columna
/// generada con la configuración <c>messaging.es_unaccent</c>, la cascada— y que la migración es idempotente.</summary>
public sealed class MessagingPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSchemaHasTheIndexesExtensionsAndConfigurationOfTheSpec()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        var indexes = await ScalarAsync<string>(connectionString,
            "SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname = 'messaging'");
        foreach (var expected in new[]
        {
            "IX_conversations_connection_user", "IX_conversations_connection_wa_legacy", "IX_conversations_tenant_status_activity",
            "IX_conversations_tenant_assignee_status_activity", "IX_conversations_tenant_unassigned_status_activity",
            "IX_conversations_tenant_customer_activity", "IX_conversations_username_trgm", "IX_conversations_tenant_open",
            "IX_conversations_tenant_unread", "IX_conversations_profile_name_trgm", "IX_conversations_wa_id_trgm",
            "IX_messages_thread", "IX_messages_connection_wamid", "IX_messages_conversation_client", "IX_messages_tenant_search",
            "IX_message_media_pending", "IX_webhook_deliveries_body_sha256", "IX_webhook_deliveries_pending",
        })
        {
            Assert.Contains(expected, indexes, StringComparison.Ordinal);
        }

        // Spec 2026-10-10 §7.1: el único viejo sobre (connection_id, wa_id) ya no existe; lo reemplaza el parcial «_legacy».
        Assert.DoesNotContain("IX_conversations_connection_wa,", indexes + ",", StringComparison.Ordinal);

        Assert.Equal(3L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_extension WHERE extname IN ('pg_trgm','unaccent','btree_gin')"));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM pg_ts_config c JOIN pg_namespace n ON n.oid = c.cfgnamespace WHERE n.nspname = 'messaging' AND c.cfgname = 'es_unaccent'"));
        Assert.Equal("{fillfactor=80}", await ScalarAsync<string>(connectionString, "SELECT reloptions::text FROM pg_class WHERE relname = 'conversations' AND relnamespace = 'messaging'::regnamespace"));
        // §7.3: además del fillfactor, el autovacuum agresivo de messages y la lista pendiente acotada del GIN.
        Assert.Equal("{fillfactor=90,autovacuum_vacuum_scale_factor=0.02}", await ScalarAsync<string>(connectionString, "SELECT reloptions::text FROM pg_class WHERE relname = 'messages' AND relnamespace = 'messaging'::regnamespace"));
        Assert.Equal("{gin_pending_list_limit=2048}", await ScalarAsync<string>(connectionString, "SELECT reloptions::text FROM pg_class WHERE relname = 'IX_messages_tenant_search' AND relnamespace = 'messaging'::regnamespace"));
        Assert.Equal("drogueri", await ScalarAsync<string>(connectionString, "SELECT (regexp_match(to_tsvector('messaging.es_unaccent', 'Droguería')::text, '''([a-z]+)'''))[1]"));
        // El stemmer Snowball de spanish quita la terminación verbal -idos: «pedidos», «pedido» y «pedir» dan «ped».
        Assert.Equal("ped", await ScalarAsync<string>(connectionString, "SELECT (regexp_match(to_tsvector('messaging.es_unaccent', 'pedidos')::text, '''([a-z]+)'''))[1]"));
    }

    // §7.2–§7.4: no basta el nombre; la definición física (método, orden, INCLUDE y predicado parcial) es
    // la que hace que cada consulta caliente sea una búsqueda acotada a cientos de millones de filas.
    [Fact]
    public async Task TheIndexesHaveTheMethodsOrderIncludeAndPredicatesOfTheSpec()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        foreach (var (name, expected) in new[]
        {
            ("IX_conversations_connection_user", "CREATE UNIQUE INDEX \"IX_conversations_connection_user\" ON messaging.conversations USING btree (connection_id, user_id) WHERE (user_id IS NOT NULL)"),
            ("IX_conversations_connection_wa_legacy", "CREATE UNIQUE INDEX \"IX_conversations_connection_wa_legacy\" ON messaging.conversations USING btree (connection_id, wa_id) WHERE (user_id IS NULL)"),
            ("IX_conversations_tenant_assignee_status_activity", "USING btree (tenant_id, assigned_member_id, status, last_activity_at DESC, id DESC) WHERE (assigned_member_id IS NOT NULL)"),
            ("IX_conversations_tenant_unassigned_status_activity", "USING btree (tenant_id, status, last_activity_at DESC, id DESC) WHERE (assigned_member_id IS NULL)"),
            ("IX_conversations_tenant_customer_activity", "USING btree (tenant_id, customer_id, last_activity_at DESC) WHERE (customer_id IS NOT NULL)"),
            ("IX_conversations_username_trgm", "USING gin (username gin_trgm_ops)"),
            ("IX_conversations_tenant_status_activity", "USING btree (tenant_id, status, last_activity_at DESC, id DESC)"),
            ("IX_conversations_tenant_open", "USING btree (tenant_id) WHERE ((status)::text = 'Open'::text)"),
            ("IX_conversations_tenant_unread", "USING btree (tenant_id) INCLUDE (unread_count) WHERE (unread_count > 0)"),
            ("IX_conversations_profile_name_trgm", "USING gin (profile_name gin_trgm_ops)"),
            ("IX_conversations_wa_id_trgm", "USING gin (wa_id gin_trgm_ops)"),
            ("IX_messages_thread", "USING btree (conversation_id, occurred_at DESC, id DESC)"),
            ("IX_messages_connection_wamid", "CREATE UNIQUE INDEX \"IX_messages_connection_wamid\" ON messaging.messages USING btree (connection_id, wamid) WHERE (wamid IS NOT NULL)"),
            ("IX_messages_conversation_client", "CREATE UNIQUE INDEX \"IX_messages_conversation_client\" ON messaging.messages USING btree (conversation_id, client_id) WHERE (client_id IS NOT NULL)"),
            ("IX_messages_tenant_search", "USING gin (tenant_id, search_vector)"),
            ("IX_message_media_pending", "USING btree (next_attempt_at) WHERE (stored_at IS NULL)"),
            ("IX_webhook_deliveries_body_sha256", "CREATE UNIQUE INDEX \"IX_webhook_deliveries_body_sha256\" ON messaging.webhook_deliveries USING btree (body_sha256)"),
            ("IX_webhook_deliveries_pending", "USING btree (id) WHERE (processed_at IS NULL)"),
        })
        {
            var definition = await ScalarAsync<string>(connectionString,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'messaging' AND indexname = @name", ("name", name));
            Assert.Contains(expected, definition, StringComparison.Ordinal);
        }

        Assert.Equal("ALWAYS", await ScalarAsync<string>(connectionString,
            "SELECT is_generated FROM information_schema.columns WHERE table_schema = 'messaging' AND table_name = 'messages' AND column_name = 'search_vector'"));
        Assert.Equal(
            "to_tsvector('messaging.es_unaccent'::regconfig, ((COALESCE(text, ''::text) || ' '::text) || (COALESCE(caption, ''::character varying))::text))",
            await ScalarAsync<string>(connectionString,
                "SELECT generation_expression FROM information_schema.columns WHERE table_schema = 'messaging' AND table_name = 'messages' AND column_name = 'search_vector'"));
        // Las extensiones viven en public (§7.2): con SCHEMA messaging, gin_trgm_ops saldría del search_path.
        Assert.Equal("public,public,public", await ScalarAsync<string>(connectionString,
            "SELECT string_agg(n.nspname, ',' ORDER BY e.extname) FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname IN ('pg_trgm','unaccent','btree_gin')"));
    }

    // §7.3: el bloque DO … IF NOT EXISTS se puede correr otra vez sobre la misma base.
    [Fact]
    public async Task TheSearchConfigurationBlockIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        await ExecuteAsync(connectionString, Modules.Messaging.Infrastructure.Persistence.Migrations.InitialMessaging.SearchConfigurationSql);
        await ExecuteAsync(connectionString, Modules.Messaging.Infrastructure.Persistence.Migrations.InitialMessaging.SearchConfigurationSql);

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_ts_config WHERE cfgname = 'es_unaccent'"));
    }

    [Fact]
    public async Task TheChecksRejectAnInboundFailedAndAnUnknownKind()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, connectionId, "573001234567");

        var inboundFailed = await Assert.ThrowsAsync<PostgresException>(() => InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, direction: 1, kind: 1, status: 4));
        Assert.Equal("CK_messages_inbound_not_failed", inboundFailed.ConstraintName);
        var unknownKind = await Assert.ThrowsAsync<PostgresException>(() => InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, direction: 1, kind: 14, status: 2));
        Assert.Equal("CK_messages_kind", unknownKind.ConstraintName);
    }

    // Spec 2026-10-10 §7.1: lo que sólo la base hace cumplir de la identidad, la asignación y los eventos.
    [Fact]
    public async Task TheNewChecksHoldIdentityAssignmentAndEventShape()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedBsuidConversationAsync(factory, connectionString, tenantId, connectionId, "CO.1", null);

        var noIdentity = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET user_id = NULL WHERE id = @id", ("id", conversationId)));
        Assert.Equal("CK_conversations_identity", noIdentity.ConstraintName);
        var halfAssigned = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET assigned_member_id = gen_random_uuid() WHERE id = @id", ("id", conversationId)));
        Assert.Equal("CK_conversations_assignment", halfAssigned.ConstraintName);
        // Con details para que cumpla CK_messages_event_shape: PostgreSQL evalúa los CHECK por nombre y, sin él,
        // reportaría ese primero en vez del que esta fila prueba.
        var systemText = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            """INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, details, created_at) VALUES (gen_random_uuid(), @c, @t, @n, now(), 3, 1, 2, '{"type":"Taken"}', now())""",
            ("c", conversationId), ("t", tenantId), ("n", connectionId)));
        Assert.Equal("CK_messages_system_is_event", systemText.ConstraintName);
        var eventWithoutDetails = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, created_at) VALUES (gen_random_uuid(), @c, @t, @n, now(), 3, 13, 2, now())",
            ("c", conversationId), ("t", tenantId), ("n", connectionId)));
        Assert.Equal("CK_messages_event_shape", eventWithoutDetails.ConstraintName);
        Assert.Equal(1, await ExecuteAsync(connectionString,
            """INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, details, created_at) VALUES (gen_random_uuid(), @c, @t, @n, now(), 3, 13, 2, '{"type":"Taken"}', now())""",
            ("c", conversationId), ("t", tenantId), ("n", connectionId)));
        // Dos conversaciones con BSUID pueden compartir teléfono (número reciclado); dos viejas no.
        await SeedBsuidConversationAsync(factory, connectionString, tenantId, connectionId, "CO.2", "573001234567");
        await SeedBsuidConversationAsync(factory, connectionString, tenantId, connectionId, "CO.3", "573001234567");
        await SeedConversationAsync(factory, tenantId, connectionId, "573001234567");
        await Assert.ThrowsAnyAsync<Exception>(() => SeedConversationAsync(factory, tenantId, connectionId, "573001234567"));
    }

    [Fact]
    public async Task TheWamidIsUniquePerConnectionAndTheConversationCascades()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, connectionId, "573001234567");
        await InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, 1, 1, 2, wamid: "wamid.1");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, 1, 1, 2, wamid: "wamid.1"));
        Assert.Equal("IX_messages_connection_wamid", duplicate.ConstraintName);
        await InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, 1, 1, 2, wamid: null);
        await InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, 1, 1, 2, wamid: null);

        await ExecuteAsync(connectionString, "DELETE FROM messaging.conversations WHERE id = @id", ("id", conversationId));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.messages"));
    }

    [Fact]
    public async Task ResolveWithAStaleVersionIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, Guid.CreateVersion7(), "573001234567");

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
        var conversation = await repository.FindAsync(tenantId, conversationId, Ct);
        Assert.NotNull(conversation);
        // La carrera: otro pod (la ingesta, §7.5) sube la versión después de que este scope leyó la fila.
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET version = version + 1 WHERE id = @id", ("id", conversationId));
        conversation.Resolve(DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<BuildingBlocks.Application.RequestConcurrencyException>(() =>
            scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Null(await repository.FindAsync(Guid.CreateVersion7(), conversationId, Ct));
    }

    // §8.4: marcar leído es un solo UPDATE sin token de concurrencia. Sube version una vez, devuelve lo que
    // necesita el acuse a Meta y, con el contador ya en 0, no toca la fila. Una ingesta que sube version por
    // SQL (§7.5) en medio ya no puede convertir un read en 412.
    [Fact]
    public async Task MarkReadIsOneAtomicUpdateThatBumpsTheVersionOnce()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, connectionId, "573001234567");
        var inboundAt = new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero);
        var now = inboundAt.AddMinutes(5);
        // La ingesta ya pasó (y subió version) antes de que llegue el read.
        await ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET unread_count = 3, last_inbound_wamid = 'wamid.in', last_inbound_at = @at, version = version + 1 WHERE id = @id",
            ("at", inboundAt), ("id", conversationId));
        var versionBefore = await ScalarAsync<long>(connectionString, "SELECT version FROM messaging.conversations WHERE id = @id", ("id", conversationId));

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
        var target = await repository.MarkReadAsync(tenantId, conversationId, now, Ct);
        var again = await repository.MarkReadAsync(tenantId, conversationId, now.AddMinutes(1), Ct);
        var otherTenant = await repository.MarkReadAsync(Guid.CreateVersion7(), conversationId, now, Ct);

        Assert.Equal(new ReadReceiptTarget(connectionId, "wamid.in", inboundAt), target);
        Assert.Null(again);
        Assert.Null(otherTenant);
        Assert.Equal($"0|{versionBefore + 1}", await ScalarAsync<string>(connectionString,
            "SELECT unread_count || '|' || version FROM messaging.conversations WHERE id = @id", ("id", conversationId)));
        Assert.Equal(now.UtcDateTime, await ScalarAsync<DateTime>(connectionString,
            "SELECT updated_at FROM messaging.conversations WHERE id = @id", ("id", conversationId)));
        Assert.True(await repository.ExistsAsync(tenantId, conversationId, Ct));
        Assert.False(await repository.ExistsAsync(Guid.CreateVersion7(), conversationId, Ct));
    }

    private static Task<int> InsertMessageAsync(
        string connectionString, Guid conversationId, Guid tenantId, Guid connectionId, int direction, int kind, int status, string? wamid = "wamid.x")
        => ExecuteAsync(
            connectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, wamid, created_at)
            VALUES (@id, @conversationId, @tenantId, @connectionId, now(), @direction, @kind, @status, 'hola', @wamid, now())
            """,
            ("id", Guid.CreateVersion7()), ("conversationId", conversationId), ("tenantId", tenantId), ("connectionId", connectionId),
            ("direction", (short)direction), ("kind", (short)kind), ("status", (short)status), ("wamid", (object?)wamid ?? DBNull.Value));
}
