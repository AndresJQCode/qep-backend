using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Siembra conversaciones y mensajes de Mensajería por SQL, como las pruebas de Pipeline y de Customers que leen conversaciones:
/// Soporte sólo las lee por sus adaptadores, así que la prueba no necesita pasar por la ingesta de Meta. Los códigos de
/// <c>messaging.messages</c> son los de <c>MessageColumnCodes</c>: dirección 1 entrante, 2 saliente, 3 evento; tipo 1 texto.
/// </summary>
internal static class SupportMessagingSeed
{
    public const short Inbound = 1;

    public const short Outbound = 2;

    public const short TextKind = 1;

    /// <summary>El toque de un botón de respuesta rápida (<c>MessageKind.Interactive</c>).</summary>
    public const short InteractiveKind = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Una conversación de WhatsApp (con <paramref name="waId"/>) o de Instagram (con <paramref name="userId"/> y sin
    /// <c>wa_id</c>). La conexión es un id suelto: <c>messaging.conversations</c> no tiene FK a Integrations.</summary>
    public static async Task<Guid> SeedConversationAsync(
        QepApiFactory factory,
        Guid tenantId,
        string channel = "WhatsApp",
        Guid? customerId = null,
        string? waId = "573001234567",
        string? userId = null,
        string? profileName = "Ana Gómez",
        string? username = null,
        bool blocked = false,
        DateTimeOffset? lastInboundAt = null,
        Guid? connectionId = null)
    {
        var id = Guid.CreateVersion7();
        // @wa is cast in the CASE: a null wa_id (Instagram) goes out as an untyped parameter, and Postgres would deduce
        // varchar from the wa_id column and text from the CASE (42P08, inconsistent types deduced for the parameter).
        await ExecuteAsync(
            factory,
            """
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, channel, status, last_activity_at, created_at, updated_at, version,
                customer_id, wa_id, user_id, profile_name, username, last_inbound_at, blocked_at, blocked_by_member_id, blocked_wa_id)
            VALUES (@id, @tenant, @connection, @channel, 'Open', now(), now(), now(), 1, @customer, @wa, @user, @profile, @username, @lastInbound,
                CASE WHEN @blocked THEN now() END, CASE WHEN @blocked THEN gen_random_uuid() END, CASE WHEN @blocked THEN @wa::varchar END)
            """,
            ("id", id), ("tenant", tenantId), ("connection", connectionId ?? Guid.CreateVersion7()), ("channel", channel),
            ("customer", customerId), ("wa", waId), ("user", userId), ("profile", profileName), ("username", username),
            ("lastInbound", lastInboundAt), ("blocked", blocked));
        return id;
    }

    /// <summary>Un mensaje del hilo, de texto salvo que se pida otro <paramref name="kind"/> (10 es el toque de un botón).
    /// <paramref name="imported"/> es el historial de coexistencia; <paramref name="replyTo"/> es el mensaje que cita (el
    /// <c>context.id</c> de Meta ya resuelto).</summary>
    public static async Task<Guid> SeedMessageAsync(
        QepApiFactory factory,
        Guid tenantId,
        Guid conversationId,
        DateTimeOffset occurredAt,
        short direction = Inbound,
        bool imported = false,
        string? text = "Hola, tengo un problema con mi pedido",
        Guid? messageId = null,
        short kind = TextKind,
        Guid? replyTo = null)
    {
        var id = messageId ?? Guid.CreateVersion7();
        await ExecuteAsync(
            factory,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, imported,
                created_at, reply_to_message_id)
            SELECT @id, c.id, c.tenant_id, c.connection_id, @occurred, @direction, @kind, 2, @text, @imported, now(), @replyTo
            FROM messaging.conversations c WHERE c.id = @conversation AND c.tenant_id = @tenant
            """,
            ("id", id), ("conversation", conversationId), ("tenant", tenantId), ("occurred", occurredAt), ("direction", direction),
            ("text", text), ("imported", imported), ("kind", kind), ("replyTo", replyTo));
        return id;
    }

    /// <summary>Cuántos eventos de ticket (<c>details-&gt;&gt;'type'</c>) tiene el hilo para ese ticket.</summary>
    public static async Task<long> CountTicketEventsAsync(QepApiFactory factory, Guid conversationId, Guid ticketId, string type)
    {
        await using var connection = await OpenAsync(factory);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM messaging.messages
            WHERE conversation_id = @conversation AND direction = 3 AND details->>'type' = @type AND details->>'ticketId' = @ticket
            """,
            connection);
        command.Parameters.AddWithValue("conversation", conversationId);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("ticket", ticketId.ToString());
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    public static async Task<long> ScalarAsync(QepApiFactory factory, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(factory);
        await using var command = Command(connection, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<string?> TextAsync(QepApiFactory factory, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(factory);
        await using var command = Command(connection, sql, parameters);
        return (await command.ExecuteScalarAsync(Ct)) as string;
    }

    public static async Task ExecuteAsync(QepApiFactory factory, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(factory);
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<NpgsqlConnection> OpenAsync(QepApiFactory factory)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
