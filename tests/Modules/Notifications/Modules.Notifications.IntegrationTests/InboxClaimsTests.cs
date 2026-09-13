using Microsoft.Extensions.DependencyInjection;
using Modules.Notifications.Infrastructure.Persistence;
using Npgsql;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// El reclamo por mensaje (spec 2026-09-13, A1) contra Postgres de verdad. Lo que se prueba es la
/// sentencia: la PK da un solo ganador, y el lease decide cuándo se puede retomar un mensaje.
/// No hace falta una fila en el outbox, porque el inbox no tiene FK hacia él.
/// </summary>
public sealed class InboxClaimsTests
{
    private const string Consumer = "notifications.test-consumer";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheFirstClaimWinsWithOneAttemptAndALease()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();

        Assert.Equal(1, await ClaimAsync(factory, messageId, Now));

        Assert.Equal(
            new InboxRow(null, Now + Lease, 1),
            await FindInboxAsync(database.GetConnectionString(), Consumer, messageId));
    }

    [Fact]
    public async Task WhileTheLeaseIsAliveASecondClaimGetsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();
        await ClaimAsync(factory, messageId, Now);

        Assert.Null(await ClaimAsync(factory, messageId, Now.AddMinutes(1)));

        Assert.Equal(1, (await FindInboxAsync(database.GetConnectionString(), Consumer, messageId))?.Attempts);
    }

    // Un worker que reclamó y murió: vencido el lease, otro lo retoma y el intento cuenta.
    [Fact]
    public async Task AClaimWithTheLeaseExpiredIsRetakenAndCountsTheAttempt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();
        await ClaimAsync(factory, messageId, Now);
        var later = Now + Lease + TimeSpan.FromSeconds(1);

        Assert.Equal(2, await ClaimAsync(factory, messageId, later));

        Assert.Equal(
            new InboxRow(null, later + Lease, 2),
            await FindInboxAsync(database.GetConnectionString(), Consumer, messageId));
    }

    [Fact]
    public async Task AProcessedMessageIsNeverClaimedAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();
        // Fuerza el arranque del host (y con él, NotificationsDatabaseInitializer) antes del INSERT
        // crudo de más abajo: a diferencia de los otros casos, acá no hay un ClaimAsync previo que lo
        // dispare, y sin el esquema creado el INSERT falla con "relation does not exist".
        _ = factory.Services;
        await PutInboxAsync(
            database.GetConnectionString(), Consumer, messageId,
            processedAt: Now, claimedUntil: Now + Lease, attempts: 1);
        var before = await FindInboxAsync(database.GetConnectionString(), Consumer, messageId);

        Assert.Null(await ClaimAsync(factory, messageId, Now.AddDays(1)));

        // El reclamo fallido no toca la fila: ni processed_at, ni el lease, ni los intentos.
        Assert.Equal(new InboxRow(Now, Now + Lease, 1), before);
        Assert.Equal(before, await FindInboxAsync(database.GetConnectionString(), Consumer, messageId));
    }

    // Dos réplicas a la vez, con la superposición forzada: la primera reclama dentro de una transacción
    // que todavía no commitea, así que la segunda tiene que esperarla en la PK. Cuando la primera
    // commitea, el ON CONFLICT de la segunda ve un lease vivo y no devuelve nada.
    [Fact]
    public async Task TwoConcurrentClaimsHaveExactlyOneWinner()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();
        // Fuerza el arranque del host (y las migraciones) antes del SQL crudo.
        _ = factory.Services;

        await using var first = new NpgsqlConnection(database.GetConnectionString());
        await first.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await first.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var claim = new NpgsqlCommand(
            """
            INSERT INTO notifications.inbox_messages (consumer, message_id, claimed_until, attempts)
            VALUES (@consumer, @messageId, @leaseUntil, 1)
            ON CONFLICT (consumer, message_id) DO UPDATE
                SET claimed_until = @leaseUntil,
                    attempts = inbox_messages.attempts + 1
                WHERE inbox_messages.processed_at IS NULL
                  AND inbox_messages.claimed_until < @now
            RETURNING attempts
            """,
            first,
            transaction))
        {
            claim.Parameters.AddWithValue("consumer", Consumer);
            claim.Parameters.AddWithValue("messageId", messageId);
            claim.Parameters.AddWithValue("leaseUntil", Now + Lease);
            claim.Parameters.AddWithValue("now", Now);
            Assert.Equal(1, (int)(await claim.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }

        var second = ClaimAsync(factory, messageId, Now);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted, "The second claim must wait for the first one's commit.");

        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        Assert.Null(await second);
        Assert.Equal(1, await CountInboxRowsAsync(database.GetConnectionString(), messageId));
        Assert.Equal(
            new InboxRow(null, Now + Lease, 1),
            await FindInboxAsync(database.GetConnectionString(), Consumer, messageId));
    }

    private static async Task<long> CountInboxRowsAsync(string connectionString, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM notifications.inbox_messages
            WHERE consumer = @consumer AND message_id = @messageId
            """,
            connection);
        command.Parameters.AddWithValue("consumer", Consumer);
        command.Parameters.AddWithValue("messageId", messageId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<int?> ClaimAsync(NotificationsApiFactory factory, Guid messageId, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        return await InboxClaims.TryClaimAsync(
            dbContext, Consumer, messageId, now, Lease, TestContext.Current.CancellationToken);
    }
}
