using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.IntegrationTests;

public sealed class BuyerPersistenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMigrationCreatesTheBuyersSchema()
    {
        await using var api = await BuyersApiHarness.OpenAsync();

        foreach (var table in new[] { "accounts", "profiles", "sessions", "email_tokens", "consents" })
        {
            var count = await api.ScalarAsync<long>(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'buyers' AND table_name = $1", table);
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task ADuplicateEmailIsTranslatedByIndexName()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        await using var first = api.Factory.Services.CreateAsyncScope();
        await using var second = api.Factory.Services.CreateAsyncScope();
        first.ServiceProvider.GetRequiredService<IBuyerAccountRepository>()
            .Add(BuyerAccount.Register(Guid.NewGuid(), "ana@example.com", "hash-1", Now));
        second.ServiceProvider.GetRequiredService<IBuyerAccountRepository>()
            .Add(BuyerAccount.Register(Guid.NewGuid(), "ana@example.com", "hash-2", Now));

        await first.ServiceProvider.GetRequiredService<IBuyersUnitOfWork>().SaveChangesAsync(Ct);
        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => second.ServiceProvider.GetRequiredService<IBuyersUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("store.buyer.email_taken", exception.Code);
    }

    [Fact]
    public async Task ConsumingATokenTwiceSucceedsOnce()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var accountId = await SeedAccountAsync(api, "tokens@example.com");
        const string hash = "a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4";
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IBuyerEmailTokenRepository>().Add(
                BuyerEmailToken.Issue(Guid.NewGuid(), accountId, BuyerEmailTokenPurpose.Verify, hash, Now));
            await scope.ServiceProvider.GetRequiredService<IBuyersUnitOfWork>().SaveChangesAsync(Ct);
        }

        async Task<Guid?> ConsumeAsync()
        {
            await using var scope = api.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IBuyerEmailTokenRepository>()
                .TryConsumeAsync(hash, BuyerEmailTokenPurpose.Verify, Now, Ct);
        }

        var results = await Task.WhenAll(ConsumeAsync(), ConsumeAsync());

        Assert.Single(results, id => id is not null);
        Assert.Equal(accountId, results.Single(id => id is not null));
    }

    [Fact]
    public async Task AnExpiredTokenIsNotConsumed()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var accountId = await SeedAccountAsync(api, "expired@example.com");
        const string hash = "b1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4";
        await using var scope = api.Factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IBuyerEmailTokenRepository>();
        tokens.Add(BuyerEmailToken.Issue(
            Guid.NewGuid(), accountId, BuyerEmailTokenPurpose.Reset, hash, Now - TimeSpan.FromHours(2)));
        await scope.ServiceProvider.GetRequiredService<IBuyersUnitOfWork>().SaveChangesAsync(Ct);

        var consumed = await tokens.TryConsumeAsync(hash, BuyerEmailTokenPurpose.Reset, Now, Ct);

        Assert.Null(consumed);
    }

    [Fact]
    public async Task DeletingAnAccountCascadesToProfileSessionsAndTokensButNotConsents()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var accountId = Guid.NewGuid();
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            services.GetRequiredService<IBuyerAccountRepository>()
                .Add(BuyerAccount.Register(accountId, "cascade@example.com", "hash", Now));
            services.GetRequiredService<IBuyerProfileRepository>()
                .Add(BuyerProfile.Create(accountId, NewProfileData(), Now));
            services.GetRequiredService<IBuyerSessionRepository>().Add(BuyerSession.Issue(
                Guid.NewGuid(), accountId, "tienda.lulocrm.com", new string('c', 64), Now, "203.0.113.9"));
            services.GetRequiredService<IBuyerEmailTokenRepository>().Add(BuyerEmailToken.Issue(
                Guid.NewGuid(), accountId, BuyerEmailTokenPurpose.Verify, new string('d', 64), Now));
            services.GetRequiredService<IBuyerConsentRepository>().Add(BuyerConsent.Record(
                Guid.NewGuid(), accountId, "cascade@example.com", "203.0.113.9", "tienda.lulocrm.com", Now));
            await services.GetRequiredService<IBuyersUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IBuyerAccountRepository>();
            accounts.Remove((await accounts.FindAsync(accountId, Ct))!);
            await scope.ServiceProvider.GetRequiredService<IBuyersUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.profiles WHERE account_id = $1", accountId));
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.sessions WHERE account_id = $1", accountId));
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.email_tokens WHERE account_id = $1", accountId));
        Assert.Equal(1, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.consents WHERE account_id = $1", accountId));
    }

    [Fact]
    public async Task TheProfileRoundTripsAsJson()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var accountId = await SeedAccountAsync(api, "profile@example.com");
        var data = NewProfileData();
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IBuyerProfileRepository>()
                .Add(BuyerProfile.Create(accountId, data, Now));
            await scope.ServiceProvider.GetRequiredService<IBuyersUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var reading = api.Factory.Services.CreateAsyncScope();
        var profile = await reading.ServiceProvider.GetRequiredService<IBuyerProfileRepository>()
            .FindAsync(accountId, Ct);

        Assert.NotNull(profile);
        Assert.Equal(data, profile.Data);
    }

    [Fact]
    public async Task TheOutboxRowIsWrittenWithTheCaller()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IBuyerOutbox>()
                .Add("buyers.test-event.v1", new { email = "x@example.com" }, Now);
            await scope.ServiceProvider.GetRequiredService<IBuyersUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(1, await api.ScalarAsync<long>(
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = $1 AND attempts = 0", "buyers.test-event.v1"));
        var payloads = await api.OutboxPayloadsAsync("buyers.test-event.v1");
        Assert.Equal("x@example.com", payloads.Single()["email"]!.GetValue<string>());
    }

    private static BuyerProfileData NewProfileData() => new(
        new BuyerBillingData(
            "Ana Gómez", "CC", "1234567", "3001234567", "ana@example.com", "Cra 1 # 2-3", "CO",
            Guid.Parse("01900000-0000-7000-8000-00000000aaaa"), Guid.Parse("01900000-0000-7000-8000-00000000bbbb"), "Rionegro"),
        new BuyerShippingData("Luis Gómez", "3009876543", "Calle 4 # 5-6", "CO", null, null, "Medellín"),
        ShippingSameAsBilling: false);

    private static async Task<Guid> SeedAccountAsync(BuyersApiHarness.BuyersApi api, string email)
    {
        var id = Guid.NewGuid();
        await using var scope = api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IBuyerAccountRepository>()
            .Add(BuyerAccount.Register(id, email, "hash", Now));
        await scope.ServiceProvider.GetRequiredService<IBuyersUnitOfWork>().SaveChangesAsync(Ct);
        return id;
    }
}
