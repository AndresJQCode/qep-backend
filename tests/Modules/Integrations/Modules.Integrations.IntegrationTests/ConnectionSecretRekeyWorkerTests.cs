using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.SecretProtection;
using Npgsql;
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

    private static Task<string> InstallKeyIdAsync(string connectionString, Guid installId) =>
        ScalarAsync<string>(connectionString, "SELECT key_id FROM integrations.shopify_installs WHERE id = @id", ("id", installId));

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

    // Spec 2026-10-15 §9: una instalación canjeada guarda el token hasta 7 días; rotar la llave no la puede dejar sin enlace.
    [Fact]
    public async Task RekeyIncludesShopifyInstalls()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler);

        Guid installId;
        using (var oldHost = factory.WithSecretProtection("old", ("old", OldKey)))
        {
            await AwaitFirstRunAsync(oldHost);
            var tenant = await RegisterTenantAsync(oldHost);
            using var owner = CreateClient(oldHost, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
            installId = await ShopifyFlow.InstallAsync(oldHost, owner, tenant.TenantId);
        }

        Assert.Equal("old", await InstallKeyIdAsync(connectionString, installId));
        var expiresAt = await ScalarAsync<DateTime>(
            connectionString, "SELECT expires_at FROM integrations.shopify_installs WHERE id = @id", ("id", installId));

        var logs = new CapturedLogs();
        using (var newHost = factory
                   .WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey))
                   .WithCapturedLogs(logs))
        {
            await AwaitFirstRunAsync(newHost);

            Assert.Equal("test", await InstallKeyIdAsync(connectionString, installId));
            var ciphertext = await ScalarAsync<byte[]>(
                connectionString, "SELECT access_token_protected FROM integrations.shopify_installs WHERE id = @id", ("id", installId));
            using var scope = newHost.Services.CreateScope();
            Assert.True(scope.ServiceProvider.GetRequiredService<ISecretProtector>().TryUnprotect(
                installId, ShopifyFieldKeys.AccessToken, new ProtectedSecret("test", ciphertext), out var plaintext));
            Assert.Equal(ShopifyFlow.SentinelAccessToken, plaintext);

            // Spec 2026-11-12 D5: el refresh token se re-cifra con el access token, con la misma llave.
            var refreshCiphertext = await ScalarAsync<byte[]>(
                connectionString, "SELECT refresh_token_protected FROM integrations.shopify_installs WHERE id = @id", ("id", installId));
            Assert.True(scope.ServiceProvider.GetRequiredService<ISecretProtector>().TryUnprotect(
                installId, ShopifyFieldKeys.RefreshToken, new ProtectedSecret("test", refreshCiphertext), out var refreshPlaintext));
            Assert.Equal(ShopifyFlow.SentinelRefreshToken, refreshPlaintext);

            // Idempotente: la segunda corrida ya no la toca.
            Assert.Equal(new RekeyRunResult(0, 0), await WorkerOf(newHost).RunOnceAsync(Ct));
        }

        Assert.Equal(expiresAt, await ScalarAsync<DateTime>(
            connectionString, "SELECT expires_at FROM integrations.shopify_installs WHERE id = @id", ("id", installId)));
        Assert.Contains(logs.Entries, entry => entry.Contains("rekey finished: 1 re-encrypted, 0 skipped", StringComparison.Ordinal));
        Assert.DoesNotContain(ShopifyFlow.SentinelAccessToken, logs.AllText, StringComparison.Ordinal);
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

    private const string RotatedAccess = "shpat_rotated-SENTINEL-4b5c";
    private const string RotatedRefresh = "shprt_rotated-SENTINEL-4b5c";

    /// <summary>
    /// Abre, en una conexión propia, la transacción de quien está rotando el par (el enlace que refresca una instalación o
    /// el worker que refresca una conexión): toma el candado del token y escribe el par nuevo sin confirmar, así que también
    /// tiene el candado de la fila. Luego arranca <paramref name="host"/> y espera a que su primera corrida de re-cifrado
    /// termine o quede bloqueada en esa fila; sólo entonces confirma. Así la carrera «lee el par viejo, guarda después del
    /// refresco» se da siempre, no por suerte.
    /// </summary>
    private static async Task RotateWhileTheFirstRekeyRunsAsync(
        string connectionString, WebApplicationFactory<Program> host, Guid ownerId, string writeSql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(20261112, hashtext(@owner))", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("owner", ownerId.ToString());
            await lockCommand.ExecuteNonQueryAsync(Ct);
        }

        await using (var write = new NpgsqlCommand(writeSql, connection, transaction))
        {
            foreach (var (name, value) in parameters)
            {
                write.Parameters.AddWithValue(name, value);
            }

            Assert.True(await write.ExecuteNonQueryAsync(Ct) > 0);
        }

        var firstRun = WorkerOf(host).FirstRunCompletion;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!firstRun.IsCompleted
               && await ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_locks WHERE NOT granted") == 0)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The rekey neither finished nor blocked.");
            await Task.Delay(50, Ct);
        }

        await transaction.CommitAsync(Ct);
        await firstRun.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    private static async Task<(string Access, string Refresh)> InstallTokensAsync(
        WebApplicationFactory<Program> host, string connectionString, Guid installId)
    {
        var keyId = await InstallKeyIdAsync(connectionString, installId);
        var access = await ScalarAsync<byte[]>(
            connectionString, "SELECT access_token_protected FROM integrations.shopify_installs WHERE id = @id", ("id", installId));
        var refresh = await ScalarAsync<byte[]>(
            connectionString, "SELECT refresh_token_protected FROM integrations.shopify_installs WHERE id = @id", ("id", installId));
        using var scope = host.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        Assert.True(protector.TryUnprotect(installId, ShopifyFieldKeys.AccessToken, new ProtectedSecret(keyId, access), out var accessPlain));
        Assert.True(protector.TryUnprotect(installId, ShopifyFieldKeys.RefreshToken, new ProtectedSecret(keyId, refresh), out var refreshPlain));
        return (accessPlain, refreshPlain);
    }

    // Revisión del bloque A: el re-cifrado leía la instalación, el enlace la refrescaba bajo el candado del token y el
    // re-cifrado guardaba después el par viejo, que Shopify ya había retirado. Ahora toma el mismo candado sin esperar y
    // relee bajo él: ocupado, salta la fila y la re-cifra en la corrida siguiente, ya con el par nuevo.
    [Fact]
    public async Task RekeyNeverWritesBackTheInstallPairARefreshReplaced()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler);

        Guid installId;
        byte[] rotatedAccess, rotatedRefresh;
        using (var oldHost = factory.WithSecretProtection("old", ("old", OldKey)))
        {
            await AwaitFirstRunAsync(oldHost);
            var tenant = await RegisterTenantAsync(oldHost);
            using var owner = CreateClient(oldHost, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
            installId = await ShopifyFlow.InstallAsync(oldHost, owner, tenant.TenantId);
            var protector = oldHost.Services.GetRequiredService<ISecretProtector>();
            rotatedAccess = protector.Protect(installId, ShopifyFieldKeys.AccessToken, RotatedAccess).Ciphertext;
            rotatedRefresh = protector.Protect(installId, ShopifyFieldKeys.RefreshToken, RotatedRefresh).Ciphertext;
        }

        using var newHost = factory.WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey));
        await RotateWhileTheFirstRekeyRunsAsync(
            connectionString,
            newHost,
            installId,
            "UPDATE integrations.shopify_installs SET access_token_protected = @access, refresh_token_protected = @refresh WHERE id = @id",
            ("access", rotatedAccess),
            ("refresh", rotatedRefresh),
            ("id", installId));
        await WorkerOf(newHost).RunOnceAsync(Ct);

        Assert.Equal("test", await InstallKeyIdAsync(connectionString, installId));
        Assert.Equal((RotatedAccess, RotatedRefresh), await InstallTokensAsync(newHost, connectionString, installId));
    }

    // El análogo de la conexión ya lo cubre la concurrencia optimista: el refresco del worker sube la versión, así que el
    // re-cifrado que leyó antes choca, cuenta el lote como saltado y lo re-cifra en la corrida siguiente con el par nuevo.
    [Fact]
    public async Task RekeyNeverWritesBackTheConnectionPairARefreshReplaced()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler);

        Guid tenantId, connectionId;
        byte[] rotatedAccess, rotatedRefresh;
        using (var oldHost = factory.WithSecretProtection("old", ("old", OldKey)))
        {
            await AwaitFirstRunAsync(oldHost);
            var tenant = await RegisterTenantAsync(oldHost);
            tenantId = tenant.TenantId;
            using var owner = CreateClient(oldHost, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
            var linked = await SendAsync(
                owner, HttpMethod.Post, ShopifyFlow.LinkUrl(tenant.TenantId),
                new { installId = await ShopifyFlow.InstallAsync(oldHost, owner, tenant.TenantId) });
            linked.EnsureSuccessStatusCode();
            connectionId = (await linked.Content.ReadFromJsonAsync<ConnectionResponse>(Ct))!.Id;
            var protector = oldHost.Services.GetRequiredService<ISecretProtector>();
            rotatedAccess = protector.Protect(connectionId, ShopifyFieldKeys.AccessToken, RotatedAccess).Ciphertext;
            rotatedRefresh = protector.Protect(connectionId, ShopifyFieldKeys.RefreshToken, RotatedRefresh).Ciphertext;
        }

        using var newHost = factory.WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey));
        await RotateWhileTheFirstRekeyRunsAsync(
            connectionString,
            newHost,
            connectionId,
            """
            UPDATE integrations.connections SET version = version + 1 WHERE id = @id;
            UPDATE integrations.connection_secrets SET ciphertext = CASE field_key WHEN 'accessToken' THEN @access ELSE @refresh END
            WHERE connection_id = @id AND field_key IN ('accessToken', 'refreshToken');
            """,
            ("access", rotatedAccess),
            ("refresh", rotatedRefresh),
            ("id", connectionId));
        await WorkerOf(newHost).RunOnceAsync(Ct);

        using var scope = newHost.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().FindAsync(tenantId, connectionId, Ct);
        var protectorNow = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        string Plain(string fieldKey)
        {
            var secret = Assert.Single(connection!.Secrets, stored => stored.FieldKey == fieldKey);
            Assert.Equal("test", secret.KeyId);
            Assert.True(protectorNow.TryUnprotect(connectionId, fieldKey, secret.Protected, out var plaintext));
            return plaintext;
        }

        Assert.Equal(RotatedAccess, Plain(ShopifyFieldKeys.AccessToken));
        Assert.Equal(RotatedRefresh, Plain(ShopifyFieldKeys.RefreshToken));
    }
}
