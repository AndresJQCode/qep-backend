using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using Modules.Buyers.Infrastructure;
using Modules.Buyers.Infrastructure.Signup;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// La limpieza periódica de Buyers: cuentas sin verificar, sesiones y tokens vencidos, y los restos de plataforma que guardan
/// hashes y tokens en claro (R10). El worker está apagado en el harness; <c>RunOnceAsync</c> se llama a mano con un "ahora" fijo.
/// Necesitan Docker.
/// </summary>
public sealed class BuyerHousekeepingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] BuyerEvents =
    [
        BuyerSignupRequestedMessage.EventName,
        BuyerIntegrationEvents.EmailVerificationRequested,
        BuyerIntegrationEvents.ExistingAccountNotice,
        BuyerIntegrationEvents.PasswordResetRequested,
        BuyerIntegrationEvents.AccountDeleted,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<BuyerHousekeepingResult> RunAsync(BuyersApiHarness.BuyersApi api) =>
        ActivatorUtilities.CreateInstance<BuyerHousekeepingWorker>(api.Factory.Services).RunOnceAsync(Now, Ct);

    private static Task<long> CountAsync(BuyersApiHarness.BuyersApi api, string sql, params object[] args) =>
        api.ScalarAsync<long>(sql, args);

    // Cada siembra es un INSERT dentro de un CTE para reutilizar ScalarAsync.
    private static async Task<Guid> SeedAccountAsync(
        BuyersApiHarness.BuyersApi api, string email, DateTimeOffset updatedAt, DateTimeOffset? verifiedAt = null)
    {
        var id = Guid.CreateVersion7();
        await api.ScalarAsync<long>(
            """
            WITH a AS (
                INSERT INTO buyers.accounts (id, email, password_hash, email_verified_at, created_at, updated_at)
                VALUES ($1, $2, 'hash', $3, $4, $4) RETURNING 1),
            p AS (
                INSERT INTO buyers.profiles (account_id, data, updated_at)
                VALUES ($1, '{}'::jsonb, $4) RETURNING 1)
            SELECT (SELECT count(*) FROM a) + (SELECT count(*) FROM p)
            """,
            id, email, (object?)verifiedAt ?? DBNull.Value, updatedAt);
        return id;
    }

    private static Task<long> SeedSessionAsync(
        BuyersApiHarness.BuyersApi api,
        Guid accountId,
        DateTimeOffset lastSeenAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? revokedAt = null)
    {
        var id = Guid.CreateVersion7();
        return api.ScalarAsync<long>(
            """
            WITH s AS (
                INSERT INTO buyers.sessions (id, account_id, store_host, token_hash, created_at, last_seen_at, expires_at, revoked_at)
                VALUES ($1, $2, 'acme.lulocrm.com', $3, $4, $4, $5, $6) RETURNING 1)
            SELECT count(*) FROM s
            """,
            id, accountId, id.ToString("N") + id.ToString("N"), lastSeenAt, expiresAt, (object?)revokedAt ?? DBNull.Value);
    }

    private static Task<long> SeedTokenAsync(
        BuyersApiHarness.BuyersApi api, Guid accountId, DateTimeOffset expiresAt, DateTimeOffset? usedAt = null)
    {
        var id = Guid.CreateVersion7();
        return api.ScalarAsync<long>(
            """
            WITH t AS (
                INSERT INTO buyers.email_tokens (id, account_id, purpose, token_hash, created_at, expires_at, used_at)
                VALUES ($1, $2, 'Verify', $3, $4, $5, $6) RETURNING 1)
            SELECT count(*) FROM t
            """,
            id, accountId, id.ToString("N") + id.ToString("N"), Now.AddDays(-1), expiresAt, (object?)usedAt ?? DBNull.Value);
    }

    private static Task<long> SeedOutboxAsync(
        BuyersApiHarness.BuyersApi api, string eventName, DateTimeOffset occurredAt, DateTimeOffset? processedAt)
    {
        var id = Guid.CreateVersion7();
        return api.ScalarAsync<long>(
            """
            WITH o AS (
                INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts)
                VALUES ($1, $2, '{"passwordHash":"secreto","token":"crudo"}'::jsonb, 'test', $3, $4, 0) RETURNING 1)
            SELECT count(*) FROM o
            """,
            id, eventName, occurredAt, (object?)processedAt ?? DBNull.Value);
    }

    [Fact]
    public async Task AnUnverifiedAccountOlderThanSevenDaysIsPurgedAndItsEmailIsFree()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var accountId = await SeedAccountAsync(
            api, "ana@correo.com", Now - BuyerAccount.UnverifiedRetention - TimeSpan.FromMinutes(1));
        await SeedSessionAsync(api, accountId, Now, Now.AddDays(20));
        await SeedTokenAsync(api, accountId, Now.AddHours(5));

        var result = await RunAsync(api);

        Assert.Equal(1, result.Accounts);
        Assert.Equal(0, await CountAsync(api, "SELECT count(*) FROM buyers.accounts"));
        Assert.Equal(0, await CountAsync(api, "SELECT count(*) FROM buyers.profiles"));
        Assert.Equal(0, await CountAsync(api, "SELECT count(*) FROM buyers.sessions"));
        Assert.Equal(0, await CountAsync(api, "SELECT count(*) FROM buyers.email_tokens"));

        // El correo quedó libre: registrarse otra vez crea una cuenta nueva.
        var registered = await api.PostAsync(
            "/register",
            new
            {
                email = "ana@correo.com",
                password = "una-contrasena-larga",
                acceptsDataPolicy = true,
                turnstileToken = "token-ok",
                profile = (object?)null,
            });
        registered.EnsureSuccessStatusCode();
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.accounts WHERE id <> $1", accountId));
    }

    [Fact]
    public async Task AnUnverifiedAccountReRegisteredRecentlyIsKept()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        // Creada hace 30 días, pero su último registro (updated_at) fue hace 2: sigue viva.
        var accountId = await SeedAccountAsync(api, "ana@correo.com", Now.AddDays(-2));
        await api.ScalarAsync<long>(
            "WITH u AS (UPDATE buyers.accounts SET created_at = $2 WHERE id = $1 RETURNING 1) SELECT count(*) FROM u",
            accountId, Now.AddDays(-30));

        var result = await RunAsync(api);

        Assert.Equal(0, result.Accounts);
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.accounts"));
    }

    [Fact]
    public async Task AVerifiedAccountIsNeverPurged()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        await SeedAccountAsync(api, "ana@correo.com", Now.AddDays(-400), verifiedAt: Now.AddDays(-399));

        var result = await RunAsync(api);

        Assert.Equal(0, result.Accounts);
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.accounts"));
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.profiles"));
    }

    [Fact]
    public async Task ExpiredRevokedAndIdleSessionsArePurgedButLiveOnesStay()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var accountId = await SeedAccountAsync(api, "ana@correo.com", Now, verifiedAt: Now.AddDays(-1));
        await SeedSessionAsync(api, accountId, Now.AddHours(-1), Now.AddDays(10), revokedAt: Now.AddMinutes(-5)); // revocada
        await SeedSessionAsync(api, accountId, Now.AddHours(-1), Now.AddSeconds(-1)); // vencida
        await SeedSessionAsync(api, accountId, Now.AddDays(-8), Now.AddDays(10)); // inactiva
        await SeedSessionAsync(api, accountId, Now.AddDays(-6), Now.AddDays(10)); // viva
        await SeedSessionAsync(api, accountId, Now, Now.AddDays(30)); // viva

        var result = await RunAsync(api);

        Assert.Equal(3, result.Sessions);
        Assert.Equal(2, await CountAsync(api, "SELECT count(*) FROM buyers.sessions"));
    }

    [Fact]
    public async Task UsedAndExpiredTokensArePurged()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var accountId = await SeedAccountAsync(api, "ana@correo.com", Now, verifiedAt: Now.AddDays(-1));
        await SeedTokenAsync(api, accountId, Now.AddHours(10), usedAt: Now.AddMinutes(-3)); // usado
        await SeedTokenAsync(api, accountId, Now.AddSeconds(-1)); // vencido
        await SeedTokenAsync(api, accountId, Now.AddHours(10)); // vigente

        var result = await RunAsync(api);

        Assert.Equal(2, result.Tokens);
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.email_tokens"));
    }

    [Fact]
    public async Task ConsentsSurviveThePurge()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var accountId = await SeedAccountAsync(
            api, "ana@correo.com", Now - BuyerAccount.UnverifiedRetention - TimeSpan.FromDays(1));
        await api.ScalarAsync<long>(
            """
            WITH c AS (
                INSERT INTO buyers.consents (id, account_id, email, accepted_at, visitor_ip, store_host, policy_url, policy_version)
                VALUES ($1, $2, 'ana@correo.com', $3, '203.0.113.9', 'acme.lulocrm.com', 'https://acme.lulocrm.com/politica', 'v1')
                RETURNING 1)
            SELECT count(*) FROM c
            """,
            Guid.CreateVersion7(), accountId, Now.AddDays(-9));

        var result = await RunAsync(api);

        Assert.Equal(1, result.Accounts);
        Assert.Equal(0, await CountAsync(api, "SELECT count(*) FROM buyers.accounts"));
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.consents"));
    }

    [Fact]
    public async Task ProcessedBuyerOutboxRowsOlderThanADayArePurgedForEveryBuyerEvent()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        foreach (var eventName in BuyerEvents)
        {
            await SeedOutboxAsync(api, eventName, Now.AddHours(-25), processedAt: Now.AddHours(-24));
        }

        var result = await RunAsync(api);

        Assert.Equal(5, result.OutboxMessages);
        Assert.Equal(0, await CountAsync(api, "SELECT count(*) FROM platform.outbox_messages"));
    }

    [Fact]
    public async Task BuyerOutboxRowsThatAreRecentOrUnprocessedStay()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        await SeedOutboxAsync(
            api, BuyerIntegrationEvents.PasswordResetRequested, Now.AddHours(-23), processedAt: Now.AddHours(-22)); // reciente
        await SeedOutboxAsync(api, BuyerSignupRequestedMessage.EventName, Now.AddDays(-5), processedAt: null); // sin despachar

        var result = await RunAsync(api);

        Assert.Equal(0, result.OutboxMessages);
        Assert.Equal(2, await CountAsync(api, "SELECT count(*) FROM platform.outbox_messages"));
    }

    [Fact]
    public async Task OutboxRowsOfOtherModulesSurviveEvenWhenOld()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        await SeedOutboxAsync(api, "tenancy.tenant-created.v1", Now.AddDays(-60), processedAt: Now.AddDays(-60));
        await SeedOutboxAsync(api, BuyerIntegrationEvents.AccountDeleted, Now.AddDays(-60), processedAt: Now.AddDays(-60));

        await RunAsync(api);

        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM platform.outbox_messages"));
        Assert.Equal(
            1,
            await CountAsync(api, "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'tenancy.tenant-created.v1'"));
    }

    [Fact]
    public async Task InboxAndProcessedSignupsOlderThanThirtyDaysArePurged()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        await api.ScalarAsync<long>(
            """
            WITH i AS (
                INSERT INTO buyers.inbox_messages (consumer, message_id, claimed_until, attempts, processed_at) VALUES
                    ('buyers.signup-requested', $1, $3, 1, $3),
                    ('buyers.signup-requested', $2, $4, 1, $4) RETURNING 1),
            s AS (
                INSERT INTO buyers.processed_signups (order_request_id, processed_at) VALUES
                    ($5, $3), ($6, $4) RETURNING 1)
            SELECT (SELECT count(*) FROM i) + (SELECT count(*) FROM s)
            """,
            Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddDays(-31), Now.AddDays(-29), Guid.CreateVersion7(), Guid.CreateVersion7());

        var result = await RunAsync(api);

        Assert.Equal(1, result.InboxMessages);
        Assert.Equal(1, result.ProcessedSignups);
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.inbox_messages"));
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.processed_signups"));
    }

    [Fact]
    public async Task TheWorkerIsRegisteredAsAHostedServiceByType()
    {
        await using var api = await BuyersApiHarness.OpenAsync(runBackgroundServices: true);

        Assert.Contains(
            api.Factory.Services.GetServices<IHostedService>(),
            service => service is BuyerHousekeepingWorker);
    }
}
