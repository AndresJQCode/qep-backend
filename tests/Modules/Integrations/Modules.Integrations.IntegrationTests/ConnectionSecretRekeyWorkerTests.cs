using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.SecretProtection;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-08, «Secreto en reposo» (viene de 6612298): al arrancar y cada intervalo el worker
/// re-cifra con la activa todo secreto en otra llave configurada; lo de una llave retirada o que no
/// descifra lo deja y lo cuenta en una advertencia por corrida, sin valores; es idempotente. Las
/// afirmaciones negativas van después de esperar <see cref="ConnectionSecretRekeyWorker.FirstRunCompletion"/>:
/// un sondeo no distingue "no cambió" de "todavía no cambió".
/// </summary>
public sealed class ConnectionSecretRekeyWorkerTests
{
    private static readonly string OldKey =
        Convert.ToBase64String(Enumerable.Range(100, 32).Select(index => (byte)index).ToArray());

    private static readonly string GoneKey =
        Convert.ToBase64String(Enumerable.Range(150, 32).Select(index => (byte)index).ToArray());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConnectionSecretRekeyWorker WorkerOf(WebApplicationFactory<Program> host) =>
        host.Services.GetServices<IHostedService>().OfType<ConnectionSecretRekeyWorker>().Single();

    private static Task AwaitFirstRunAsync(WebApplicationFactory<Program> host) =>
        WorkerOf(host).FirstRunCompletion.WaitAsync(TimeSpan.FromSeconds(30), Ct);

    private static Task<string> KeyIdAsync(string connectionString, Guid connectionId) =>
        ScalarAsync<string>(
            connectionString,
            "SELECT key_id FROM integrations.connection_secrets WHERE connection_id = @id",
            ("id", connectionId));

    private static Task<long> VersionAsync(string connectionString, Guid connectionId) =>
        ScalarAsync<long>(connectionString, "SELECT version FROM integrations.connections WHERE id = @id", ("id", connectionId));

    [Fact]
    public async Task ANewActiveKeyReencryptsOldSecretsSkipsDamagedAndRetiredOnesAndIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();

        // A y B en la llave vieja; C en la vieja y dañado; E en una llave que el host nuevo ya no
        // declara (retirada); D ya en la activa.
        Guid a, b, c, e;
        using (var oldHost = factory.WithSecretProtection("old", ("old", OldKey)))
        {
            await AwaitFirstRunAsync(oldHost);
            a = await SeedConnectionAsync(oldHost, tenantId, "A");
            b = await SeedConnectionAsync(oldHost, tenantId, "B");
            c = await SeedConnectionAsync(oldHost, tenantId, "C");
        }

        using (var goneHost = factory.WithSecretProtection("gone", ("gone", GoneKey)))
        {
            await AwaitFirstRunAsync(goneHost);
            e = await SeedConnectionAsync(goneHost, tenantId, "E");
        }

        var d = await SeedConnectionAsync(factory, tenantId, "D");
        await ExecuteAsync(
            connectionString,
            "UPDATE integrations.connection_secrets SET ciphertext = set_byte(ciphertext, 20, get_byte(ciphertext, 20) # 255) WHERE connection_id = @id",
            ("id", c));
        var damaged = await ScalarAsync<byte[]>(
            connectionString, "SELECT ciphertext FROM integrations.connection_secrets WHERE connection_id = @id", ("id", c));
        var versionD = await VersionAsync(connectionString, d);

        // Segundo arranque: "test" activa, "old" todavía declarada (fase b de la rotación).
        var logs = new CapturedLogs();
        using (var newHost = factory
                   .WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey))
                   .WithCapturedLogs(logs))
        {
            await AwaitFirstRunAsync(newHost);

            Assert.Equal("test", await KeyIdAsync(connectionString, a));
            Assert.Equal("test", await KeyIdAsync(connectionString, b));
            Assert.Equal("old", await KeyIdAsync(connectionString, c));
            Assert.Equal("gone", await KeyIdAsync(connectionString, e));
            Assert.Equal(versionD, await VersionAsync(connectionString, d));

            using var scope = newHost.Services.CreateScope();
            var connection = await scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().FindAsync(tenantId, a, Ct);
            var secret = Assert.Single(connection!.Secrets);
            Assert.True(scope.ServiceProvider.GetRequiredService<ISecretProtector>()
                .TryUnprotect(a, secret.FieldKey, secret.Protected, out var plaintext));
            Assert.Equal(SentinelApiToken, plaintext);
        }

        Assert.Contains(logs.Entries, entry => entry.Contains("rekey finished: 2 re-encrypted, 2 skipped", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry => entry.Contains("2 connection secrets could not be re-encrypted", StringComparison.Ordinal));
        Assert.DoesNotContain(SentinelApiToken, logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(damaged), logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(damaged), logs.AllText, StringComparison.OrdinalIgnoreCase);

        // Tercer arranque: idempotente. A y B no cambian de versión; C y E se vuelven a saltar.
        var versionA = await VersionAsync(connectionString, a);
        var versionB = await VersionAsync(connectionString, b);
        var thirdLogs = new CapturedLogs();
        using (var thirdHost = factory
                   .WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey))
                   .WithCapturedLogs(thirdLogs))
        {
            await AwaitFirstRunAsync(thirdHost);
        }

        Assert.Equal(versionA, await VersionAsync(connectionString, a));
        Assert.Equal(versionB, await VersionAsync(connectionString, b));
        Assert.Contains(thirdLogs.Entries, entry => entry.Contains("rekey finished: 0 re-encrypted, 2 skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAnActiveKeyTheWorkerDoesNothingAndFinishes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        using var host = factory.WithSecretProtection(string.Empty).WithCapturedLogs(logs);

        await AwaitFirstRunAsync(host);

        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("rekey finished", StringComparison.Ordinal));
    }

    // Spec: "al arrancar y cada RekeyIntervalMinutes". El cuerpo del ciclo es RunOnceAsync: correrlo otra
    // vez con el host vivo agarra lo que llegó después del arranque.
    [Fact]
    public async Task EachRunPicksUpWhatArrivedAfterTheStartAndTheIntervalComesFromConfiguration()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        using var current = factory.WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey));
        await AwaitFirstRunAsync(current);
        Assert.Equal(TimeSpan.FromMinutes(60), WorkerOf(current).Interval);

        Guid late;
        using (var oldHost = factory.WithSecretProtection("old", ("old", OldKey)))
        {
            late = await SeedConnectionAsync(oldHost, tenantId, "Tarde");
        }

        var result = await WorkerOf(current).RunOnceAsync(Ct);

        Assert.Equal(1, result.Reencrypted);
        Assert.Equal("test", await KeyIdAsync(connectionString, late));

        using var tuned = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Integrations:SecretProtection:RekeyIntervalMinutes", "15"));
        Assert.Equal(TimeSpan.FromMinutes(15), WorkerOf(tuned).Interval);
    }
}
