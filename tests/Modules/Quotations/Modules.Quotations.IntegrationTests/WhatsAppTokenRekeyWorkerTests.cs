using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Infrastructure.Whatsapp;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-10-07, «Rotación», punto 3: en cada arranque el worker re-cifra con la activa toda
/// fila en otra llave, se salta la que no descifra, es idempotente y loguea sólo tenant id, key id
/// y conteos. Las afirmaciones negativas (algo NO cambió) van siempre después de esperar
/// <see cref="WhatsAppTokenRekeyWorker.Completion"/>: un sondeo no distingue "no cambió" de
/// "todavía no cambió".
/// </summary>
public sealed class WhatsAppTokenRekeyWorkerTests
{
    private static readonly string OldKey =
        Convert.ToBase64String(Enumerable.Range(100, 32).Select(index => (byte)index).ToArray());

    private static async Task AwaitRekeyAsync(WebApplicationFactory<Program> host)
    {
        var worker = host.Services.GetServices<IHostedService>().OfType<WhatsAppTokenRekeyWorker>().Single();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    private static Task<string> KeyIdAsync(string connectionString, Guid tenantId) =>
        ScalarAsync<string>(connectionString,
            $"SELECT api_token_key_id FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'");

    private static Task<long> VersionAsync(string connectionString, Guid tenantId) =>
        ScalarAsync<long>(connectionString,
            $"SELECT version FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'");

    [Fact]
    public async Task AStartWithANewActiveKeyReencryptsTheOldRowsSkipsTheDamagedOneAndIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);

        // Cuatro tenants: A y B en la llave vieja, C en la vieja y dañado, D ya en la activa.
        var tenants = new List<(Guid TenantId, Guid OwnerId)>();
        for (var index = 0; index < 4; index++)
        {
            var (tenantId, ownerId, client) = await RegisterTenantAsync(factory, SettingsPermissions);
            client.Dispose();
            tenants.Add((tenantId, ownerId));
        }

        var (a, b, c, d) = (tenants[0], tenants[1], tenants[2], tenants[3]);
        using (var oldHost = factory.WithSecretProtection("old", ("old", OldKey)))
        {
            await AwaitRekeyAsync(oldHost);
            foreach (var tenant in new[] { a, b, c })
            {
                using var client = CreateClientFor(oldHost, tenant.OwnerId, tenant.TenantId, SettingsPermissions);
                (await PutSettingsAsync(client, tenant.TenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
            }
        }

        using (var client = CreateClientFor(factory, d.OwnerId, d.TenantId, SettingsPermissions))
        {
            (await PutSettingsAsync(client, d.TenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        }

        await ExecuteAsync(
            connectionString,
            "UPDATE quotations.tenant_whatsapp_settings "
            + "SET api_token_ciphertext = set_byte(api_token_ciphertext, 20, get_byte(api_token_ciphertext, 20) # 255) "
            + $"WHERE tenant_id = '{c.TenantId}'");
        var damagedCiphertext = await ScalarAsync<byte[]>(connectionString,
            $"SELECT api_token_ciphertext FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{c.TenantId}'");
        var versionD = await VersionAsync(connectionString, d.TenantId);

        // Segundo arranque: "test" activa, "old" todavía declarada (fase b de la rotación).
        var logs = new CapturedLogs();
        using (var newHost = factory
                   .WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey))
                   .WithCapturedLogs(logs))
        {
            await AwaitRekeyAsync(newHost);

            Assert.Equal("test", await KeyIdAsync(connectionString, a.TenantId));
            Assert.Equal("test", await KeyIdAsync(connectionString, b.TenantId));
            Assert.Equal("old", await KeyIdAsync(connectionString, c.TenantId));
            Assert.Equal(versionD, await VersionAsync(connectionString, d.TenantId));

            using var client = CreateClientFor(newHost, a.OwnerId, a.TenantId, SettingsPermissions);
            var settings = await client.GetFromJsonAsync<JsonElement>(
                WhatsAppSettingsUrl(a.TenantId), TestContext.Current.CancellationToken);
            Assert.True(settings.GetProperty("apiKeyReadable").GetBoolean());
        }

        Assert.Contains(logs.Entries, entry => entry.Contains("rekey finished: 2 re-encrypted, 1 skipped", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry =>
            entry.Contains(c.TenantId.ToString(), StringComparison.Ordinal) && entry.Contains("old", StringComparison.Ordinal));
        Assert.DoesNotContain(SentinelApiKey, logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(damagedCiphertext), logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(damagedCiphertext), logs.AllText, StringComparison.OrdinalIgnoreCase);

        // Tercer arranque: idempotente. A y B no cambian de versión; C se vuelve a saltar.
        var versionA = await VersionAsync(connectionString, a.TenantId);
        var versionB = await VersionAsync(connectionString, b.TenantId);
        var thirdLogs = new CapturedLogs();
        using (var thirdHost = factory
                   .WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey))
                   .WithCapturedLogs(thirdLogs))
        {
            await AwaitRekeyAsync(thirdHost);
        }

        Assert.Equal(versionA, await VersionAsync(connectionString, a.TenantId));
        Assert.Equal(versionB, await VersionAsync(connectionString, b.TenantId));
        Assert.Contains(thirdLogs.Entries, entry => entry.Contains("rekey finished: 0 re-encrypted, 1 skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAnActiveKeyTheWorkerDoesNothingAndFinishes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        using var host = factory.WithSecretProtection(string.Empty).WithCapturedLogs(logs);

        await AwaitRekeyAsync(host);

        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("rekey finished", StringComparison.Ordinal));
    }
}
