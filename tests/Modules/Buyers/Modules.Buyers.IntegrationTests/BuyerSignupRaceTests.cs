using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.IntegrationTests;

public sealed class BuyerSignupRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoConcurrentSignupsWithTheSameNewEmailCreateOneAccount()
    {
        // Las dos altas miran el email, no lo encuentran, y recién ahí cada una sigue: así las dos intentan insertar y el índice
        // único decide, en vez de depender de que el planificador las solape.
        using var rendezvous = new Barrier(2);
        await using var api = await BuyersApiHarness.OpenAsync(services =>
        {
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(IBuyerAccountRepository));
            services.Remove(original);
            services.AddScoped<IBuyerAccountRepository>(provider => new RendezvousAccounts(
                (IBuyerAccountRepository)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!), rendezvous));
        });

        async Task SignUpAsync(string hash)
        {
            await using var scope = api.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BuyerSignup>().SignUpAsync(
                new BuyerSignupRequest("acme.lulocrm.com", "Tienda Demo", "203.0.113.10", "carrera@example.com", hash, null),
                Ct);
        }

        await Task.WhenAll(Task.Run(() => SignUpAsync("hash-uno"), Ct), Task.Run(() => SignUpAsync("hash-dos"), Ct));

        Assert.Equal(1, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.accounts"));
        // La perdedora encuentra una cuenta sin verificar y sigue esa regla: reenvía la verificación (no hay aviso de "ya tienes cuenta").
        Assert.Equal(2, (await api.OutboxPayloadsAsync("buyers.email-verification-requested.v1")).Count);
        Assert.Empty(await api.OutboxPayloadsAsync("buyers.existing-account-notice.v1"));
        Assert.Equal(2, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.consents"));
        Assert.Equal(1, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.email_tokens WHERE used_at IS NULL"));
    }

    /// <summary>Espera, una sola vez por instancia, a que la otra alta también haya buscado el email sin hallarlo.</summary>
    private sealed class RendezvousAccounts(IBuyerAccountRepository inner, Barrier rendezvous) : IBuyerAccountRepository
    {
        private bool _waited;

        public async Task<BuyerAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        {
            var found = await inner.FindByEmailAsync(normalizedEmail, cancellationToken);
            if (found is null && !_waited)
            {
                _waited = true;
                await Task.Run(() => rendezvous.SignalAndWait(TimeSpan.FromSeconds(30)), cancellationToken);
            }

            return found;
        }

        public Task<BuyerAccount?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            inner.FindAsync(id, cancellationToken);

        public void Add(BuyerAccount account) => inner.Add(account);

        public void Remove(BuyerAccount account) => inner.Remove(account);
    }
}
