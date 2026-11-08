using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// Las carreras sobre la cuenta y sus sesiones (M2, T9), deterministas: un decorador corre la operación rival justo en el punto
/// donde la carrera duele, una sola vez, y la operación bajo prueba sigue con lo que ya había leído. Necesitan Docker.
/// </summary>
public sealed class BuyerConcurrencyRaceTests
{
    private const string Email = "ana@correo.com";
    private const string Host = "acme.lulocrm.com";
    private const string FirstPassword = "una-contrasena-larga";
    private const string ResetPassword = "la-contrasena-restablecida";
    private const string OtherPassword = "otra-contrasena-del-registro";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReRegistrationRacingThePurgeOfItsAccountIsStill202()
    {
        var hooks = new RaceHooks();
        await using var api = await OpenAsync(hooks);
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(api, FirstPassword)).StatusCode);

        // La purga borra la cuenta sin verificar después de que el registro la leyó.
        hooks.ArmFoundByEmail(account => api.ScalarAsync<int>("DELETE FROM buyers.accounts WHERE id = $1", account.Id));

        var response = await RegisterAsync(api, OtherPassword);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(hooks.FoundByEmailFired);
        Assert.Equal(1, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.accounts"));
    }

    [Fact]
    public async Task AReRegistrationDoesNotUndoAConcurrentPasswordReset()
    {
        var hooks = new RaceHooks();
        await using var api = await OpenAsync(hooks);
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(api, FirstPassword)).StatusCode);
        var resetToken = await RequestResetTokenAsync(api);

        // El restablecimiento confirma (hash nuevo y email verificado) mientras el registro tiene la cuenta vieja en memoria.
        hooks.ArmFoundByEmail(async _ => Assert.True(
            (await api.PostAsync("/password/reset", new { token = resetToken, newPassword = ResetPassword })).IsSuccessStatusCode));

        var response = await RegisterAsync(api, OtherPassword);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(hooks.FoundByEmailFired);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(api, ResetPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(api, OtherPassword)).StatusCode);
    }

    [Fact]
    public async Task ARehashOnLoginDoesNotUndoAConcurrentPasswordReset()
    {
        var hooks = new RaceHooks();
        await using var api = await OpenAsync(hooks, rehashOnEveryLogin: true);
        await api.RegisterVerifiedAsync(Email, FirstPassword);
        var resetToken = await RequestResetTokenAsync(api);

        // El login verificó la contraseña vieja y quiere re-hashearla; el restablecimiento confirma justo antes.
        hooks.ArmFoundByEmail(async _ => Assert.True(
            (await api.PostAsync("/password/reset", new { token = resetToken, newPassword = ResetPassword })).IsSuccessStatusCode));

        var racing = await LoginAsync(api, FirstPassword);

        Assert.True(hooks.FoundByEmailFired);
        Assert.NotEqual(HttpStatusCode.InternalServerError, racing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(api, ResetPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(api, FirstPassword)).StatusCode);
    }

    [Fact]
    public async Task APasswordResetRevokesASessionOpenedWhileItRuns()
    {
        var hooks = new RaceHooks();
        await using var api = await OpenAsync(hooks);
        await api.RegisterVerifiedAsync(Email, FirstPassword);
        var resetToken = await RequestResetTokenAsync(api);
        string? racingSession = null;

        // Un login confirma su sesión en el momento en que el restablecimiento está por revocar.
        hooks.ArmRevoking(async () =>
        {
            var login = await LoginAsync(api, FirstPassword);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            racingSession = (string)JsonNode.Parse(await login.Content.ReadAsStringAsync(Ct))!["sessionToken"]!;
        });

        var reset = await api.PostAsync("/password/reset", new { token = resetToken, newPassword = ResetPassword });

        Assert.True(reset.IsSuccessStatusCode);
        Assert.True(hooks.RevokingFired);
        Assert.NotNull(racingSession);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.SendAsync(HttpMethod.Get, "/session", racingSession)).StatusCode);
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.sessions WHERE revoked_at IS NULL"));
    }

    private static Task<BuyersApiHarness.BuyersApi> OpenAsync(RaceHooks hooks, bool rehashOnEveryLogin = false) =>
        BuyersApiHarness.OpenAsync(services =>
        {
            Decorate<IBuyerAccountRepository>(services, (provider, inner) => new HookedAccounts(inner, hooks));
            Decorate<IBuyerSessionRepository>(services, (provider, inner) => new HookedSessions(inner, hooks));
            if (rehashOnEveryLogin)
            {
                Decorate<IBuyerPasswordHasher>(services, (provider, inner) => new AlwaysRehashHasher(inner));
            }
        });

    private static void Decorate<T>(IServiceCollection services, Func<IServiceProvider, T, T> wrap)
        where T : class
    {
        var original = services.Single(descriptor => descriptor.ServiceType == typeof(T));
        services.Remove(original);
        services.Add(new ServiceDescriptor(
            typeof(T),
            provider =>
            {
                var inner = original.ImplementationFactory is { } factory
                    ? (T)factory(provider)
                    : original.ImplementationInstance is T instance
                        ? instance
                        : (T)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
                return wrap(provider, inner);
            },
            original.Lifetime));
    }

    private static Task<HttpResponseMessage> RegisterAsync(BuyersApiHarness.BuyersApi api, string password) =>
        api.PostAsync(
            "/register",
            new { email = Email, password, acceptsDataPolicy = true, turnstileToken = "token-ok", profile = (object?)null });

    private static Task<HttpResponseMessage> LoginAsync(BuyersApiHarness.BuyersApi api, string password) =>
        api.PostAsync("/session", new { email = Email, password }, Host);

    private static async Task<string> RequestResetTokenAsync(BuyersApiHarness.BuyersApi api)
    {
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await api.PostAsync("/password/forgot", new { email = Email, turnstileToken = "token-ok" })).StatusCode);
        return (await api.OutboxPayloadsAsync(BuyerIntegrationEvents.PasswordResetRequested))[^1]["token"]!.GetValue<string>();
    }

    /// <summary>Los puntos de carrera, cada uno de un solo disparo.</summary>
    private sealed class RaceHooks
    {
        private Func<BuyerAccount, Task>? _foundByEmail;
        private Func<Task>? _revoking;

        public bool FoundByEmailFired { get; private set; }

        public bool RevokingFired { get; private set; }

        public void ArmFoundByEmail(Func<BuyerAccount, Task> hook) => _foundByEmail = hook;

        public void ArmRevoking(Func<Task> hook) => _revoking = hook;

        public async Task OnFoundByEmailAsync(BuyerAccount account)
        {
            if (Interlocked.Exchange(ref _foundByEmail, null) is { } hook)
            {
                FoundByEmailFired = true;
                await hook(account);
            }
        }

        public async Task OnRevokingAsync()
        {
            if (Interlocked.Exchange(ref _revoking, null) is { } hook)
            {
                RevokingFired = true;
                await hook();
            }
        }
    }

    private sealed class HookedAccounts(IBuyerAccountRepository inner, RaceHooks hooks) : IBuyerAccountRepository
    {
        public async Task<BuyerAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        {
            var found = await inner.FindByEmailAsync(normalizedEmail, cancellationToken);
            if (found is not null)
            {
                await hooks.OnFoundByEmailAsync(found);
            }

            return found;
        }

        public Task<BuyerAccount?> FindAsync(Guid id, CancellationToken cancellationToken) => inner.FindAsync(id, cancellationToken);

        public void Add(BuyerAccount account) => inner.Add(account);

        public void Remove(BuyerAccount account) => inner.Remove(account);
    }

    private sealed class HookedSessions(IBuyerSessionRepository inner, RaceHooks hooks) : IBuyerSessionRepository
    {
        public void Add(BuyerSession session) => inner.Add(session);

        public Task<BuyerSession?> FindByTokenHashAsync(string hash, CancellationToken cancellationToken) =>
            inner.FindByTokenHashAsync(hash, cancellationToken);

        // Lo que leyó la revocación es lo que va a revocar: la sesión rival confirma justo después.
        public async Task<IReadOnlyList<BuyerSession>> ListActiveAsync(Guid accountId, CancellationToken cancellationToken)
        {
            var active = await inner.ListActiveAsync(accountId, cancellationToken);
            await hooks.OnRevokingAsync();
            return active;
        }

        // La revocación masiva corre dentro de la transacción del guardado: la sesión rival confirma justo antes de la sentencia.
        public async Task RevokeActiveAsync(
            Guid accountId, Guid? keepSessionId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
        {
            await hooks.OnRevokingAsync();
            await inner.RevokeActiveAsync(accountId, keepSessionId, now, reason, cancellationToken);
        }
    }

    /// <summary>El hasher real, pero todo hash correcto está desactualizado: el login siempre re-hashea.</summary>
    private sealed class AlwaysRehashHasher(IBuyerPasswordHasher inner) : IBuyerPasswordHasher
    {
        public string Hash(string password) => inner.Hash(password);

        public BuyerPasswordCheck Verify(string? hash, string password) =>
            inner.Verify(hash, password) == BuyerPasswordCheck.Success ? BuyerPasswordCheck.SuccessRehashNeeded : BuyerPasswordCheck.Failed;
    }
}
