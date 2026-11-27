using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Modules.Notifications.IntegrationTests;

/// <summary>HR birthday notices: the digest payload the way Hr's outbox writes it (camelCase, <c>expiresAt</c> as an ISO 8601 text with an
/// offset) and the raw reads of <c>notifications.hr_birthday_deliveries</c>. Shared by the bell and the delivery tests.</summary>
internal static class HrBirthdayTestSupport
{
    public const string DigestEvent = "hr.birthdays.digest.v1";

    public sealed record Person(Guid EmployeeId, string EmployeeName);

    public static string DigestPayload(
        Guid tenantId,
        Guid memberId,
        string runDate = "2026-11-26",
        IReadOnlyList<Person>? today = null,
        IReadOnlyList<Person>? upcoming = null,
        string? upcomingDate = "2026-11-27",
        Guid? whatsAppConnectionId = null,
        bool email = true,
        Guid? sentByMemberId = null,
        DateTimeOffset? expiresAt = null,
        int? todayTotal = null,
        int? upcomingTotal = null)
    {
        today ??= [new Person(Guid.CreateVersion7(), "Ana Pérez")];
        upcoming ??= [new Person(Guid.CreateVersion7(), "Luis Gómez")];
        return JsonSerializer.Serialize(new
        {
            tenantId,
            memberId,
            runDate,
            today = today.Select(person => new { employeeId = person.EmployeeId, employeeName = person.EmployeeName, birthDate = "1990-11-26", age = 36 }),
            todayTotal = todayTotal ?? today.Count,
            upcoming = upcoming.Select(person => new { employeeId = person.EmployeeId, employeeName = person.EmployeeName, birthDate = "1991-11-27", age = 35 }),
            upcomingTotal = upcomingTotal ?? upcoming.Count,
            upcomingDate,
            channels = new { whatsAppConnectionId, email, sentByMemberId = sentByMemberId ?? memberId },
            // The offset form Hr's serializer may write for a DateTimeOffset; the consumer must parse it, not compare text.
            expiresAt = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(12)).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        });
    }

    public sealed record DeliveryRow(
        Guid Id,
        Guid MemberId,
        string RunDate,
        string Channel,
        Guid? ConnectionId,
        Guid SentByMemberId,
        string Payload,
        string Status,
        string? ResultCode,
        int Attempts,
        DateTimeOffset? ClaimedUntil,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? CompletedAt);

    public static async Task<IReadOnlyList<DeliveryRow>> DeliveriesAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, member_id, run_date::text, channel, connection_id, sent_by_member_id, payload::text, status, result_code, attempts,
                   claimed_until, expires_at, completed_at
            FROM notifications.hr_birthday_deliveries WHERE tenant_id = @tenant ORDER BY channel, member_id, run_date
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<DeliveryRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new DeliveryRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetGuid(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12)));
        }

        return rows;
    }

    /// <summary>The part of the digest a delivery row stores (names only), as the consumer writes it.</summary>
    public static string PartsPayload(
        IReadOnlyList<Person> today, int todayTotal, IReadOnlyList<Person> upcoming, int upcomingTotal, string? upcomingDate) =>
        JsonSerializer.Serialize(new
        {
            today = today.Select(person => new { employeeId = person.EmployeeId, employeeName = person.EmployeeName }),
            todayTotal,
            upcoming = upcoming.Select(person => new { employeeId = person.EmployeeId, employeeName = person.EmployeeName }),
            upcomingTotal,
            upcomingDate,
        });

    /// <summary>A delivery row written the way the consumer leaves it, with a chosen state.</summary>
    public static async Task<Guid> SeedDeliveryAsync(
        string connectionString,
        Guid tenantId,
        Guid memberId,
        string channel,
        Guid? connectionId,
        Guid sentByMemberId,
        string payloadJson,
        DateTimeOffset expiresAt,
        string runDate = "2026-11-26",
        int attempts = 0)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO notifications.hr_birthday_deliveries
                (id, tenant_id, member_id, run_date, channel, connection_id, sent_by_member_id, payload, status, attempts, expires_at, created_at)
            VALUES (@id, @tenant, @member, CAST(@runDate AS date), @channel, @connection, @sentBy, CAST(@payload AS jsonb), 'Pending', @attempts, @expires, @created)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("member", memberId);
        command.Parameters.AddWithValue("runDate", runDate);
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("connection", (object?)connectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("sentBy", sentByMemberId);
        command.Parameters.AddWithValue("payload", payloadJson);
        command.Parameters.AddWithValue("attempts", attempts);
        command.Parameters.AddWithValue("expires", expiresAt);
        command.Parameters.AddWithValue("created", expiresAt.AddHours(-10));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    public static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
