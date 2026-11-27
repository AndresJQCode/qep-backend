using Bootstrapper;
using Modules.Integrations.Domain;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Settings;

namespace Bootstrapper.UnitTests;

/// <summary>Tarjeta 0pQhlDt5: el adaptador que une Integrations con la comparación de Siigo. Sólo Siigo, y con usuario y clave, llega a
/// Siigo; con los demás proveedores no se toca ninguna base. El borrado en la transacción lo cubren las pruebas de integración.</summary>
public sealed class SiigoCredentialChangeGuardTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Fields(string username = "api@empresa.test") =>
        new(StringComparer.Ordinal) { [SiigoFieldKeys.Username] = username };

    private static Dictionary<string, string> Secrets(string accessKey = "clave-SENTINEL") =>
        new(StringComparer.Ordinal) { [SiigoFieldKeys.AccessKey] = accessKey };

    [Fact]
    public async Task SiigoCredentialsReachTheComparisonWithTheConnectionId()
    {
        var change = new RecordingChange { Result = true };
        var guard = new SiigoCredentialChangeGuard(change, null!, null!);

        var must = await guard.MustResetConfigurationAsync(
            IntegrationProviders.Siigo.Key, TenantId, ConnectionId, Fields(), Secrets(), confirmed: true, Ct);

        Assert.True(must);
        var call = Assert.Single(change.Calls);
        Assert.Equal((TenantId, ConnectionId, true), (call.TenantId, call.ConnectionId, call.Confirmed));
        Assert.Equal(new SiigoCredentials(ConnectionId, "api@empresa.test", "clave-SENTINEL"), call.Credentials);
    }

    [Fact]
    public async Task AnotherProviderNeverReachesSiigo()
    {
        var change = new RecordingChange { Result = true };
        var guard = new SiigoCredentialChangeGuard(change, null!, null!);

        var must = await guard.MustResetConfigurationAsync(
            IntegrationProviders.Zenvia.Key, TenantId, ConnectionId, Fields(), Secrets(), confirmed: true, Ct);

        Assert.False(must);
        Assert.Empty(change.Calls);
    }

    [Theory]
    [InlineData("", "clave")]
    [InlineData("api@empresa.test", "")]
    public async Task WithoutUsernameOrKeyThereIsNothingToRead(string username, string accessKey)
    {
        var change = new RecordingChange { Result = true };
        var guard = new SiigoCredentialChangeGuard(change, null!, null!);

        var must = await guard.MustResetConfigurationAsync(
            IntegrationProviders.Siigo.Key, TenantId, ConnectionId, Fields(username), Secrets(accessKey), confirmed: false, Ct);

        Assert.False(must);
        Assert.Empty(change.Calls);
    }

    [Fact]
    public async Task AnotherProviderDoesNotResetAnything()
    {
        var change = new RecordingChange();
        var guard = new SiigoCredentialChangeGuard(change, null!, null!);

        await guard.ResetConfigurationAsync(IntegrationProviders.Zenvia.Key, TenantId, ConnectionId, Ct);

        Assert.Equal(0, change.Resets);
    }

    private sealed record Call(Guid TenantId, Guid ConnectionId, SiigoCredentials Credentials, bool Confirmed);

    private sealed class RecordingChange : ISiigoAccountChange
    {
        public bool Result { get; init; }

        public List<Call> Calls { get; } = [];

        public int Resets { get; private set; }

        public Task<bool> MustResetAsync(
            Guid tenantId, Guid connectionId, SiigoCredentials credentials, bool confirmed, CancellationToken cancellationToken)
        {
            Calls.Add(new Call(tenantId, connectionId, credentials, confirmed));
            return Task.FromResult(Result);
        }

        public Task ResetAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
        {
            Resets++;
            return Task.CompletedTask;
        }
    }
}
