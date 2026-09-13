using Microsoft.Extensions.DependencyInjection;
using Modules.Notifications.Infrastructure.Persistence;
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

        Assert.Null(await ClaimAsync(factory, messageId, Now.AddDays(1)));
    }

    // Dos réplicas al mismo instante: la segunda espera el commit de la primera y el ON CONFLICT ve
    // un lease vivo.
    [Fact]
    public async Task TwoConcurrentClaimsHaveExactlyOneWinner()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();

        var results = await Task.WhenAll(ClaimAsync(factory, messageId, Now), ClaimAsync(factory, messageId, Now));

        Assert.Equal(1, results.Count(result => result == 1));
        Assert.Equal(1, results.Count(result => result is null));
    }

    private static async Task<int?> ClaimAsync(NotificationsApiFactory factory, Guid messageId, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        return await InboxClaims.TryClaimAsync(
            dbContext, Consumer, messageId, now, Lease, TestContext.Current.CancellationToken);
    }
}
