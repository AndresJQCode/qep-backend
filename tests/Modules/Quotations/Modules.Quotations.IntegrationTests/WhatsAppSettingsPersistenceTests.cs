using System.Text;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La configuración de WhatsApp contra Postgres (spec 2026-10-07, «Modelo de datos»): ida y vuelta
/// con la key cifrada en dos columnas de la misma fila, la key que nunca queda en claro, y dos
/// primeros guardados que chocan en la PK y salen como 412.
/// </summary>
public sealed class WhatsAppSettingsPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheOwnAccountRoundTripsWithTheKeyEncryptedWithTheActiveKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<IWhatsAppSecretProtector>();
            var repository = scope.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>();
            Assert.Null(await repository.FindAsync(tenantId, TestContext.Current.CancellationToken));

            var settings = TenantWhatsAppSettings.CreateEmpty(tenantId, Now);
            settings.Configure(
                WhatsAppMode.Own, WhatsAppProvider.Zenvia, protector.Protect(tenantId, SentinelApiKey),
                null, FromNumber, TemplateId, Now);
            repository.Add(settings);
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<IWhatsAppSecretProtector>();
            var reloaded = await scope.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>()
                .FindReadOnlyAsync(tenantId, TestContext.Current.CancellationToken);

            Assert.NotNull(reloaded);
            Assert.Equal(WhatsAppMode.Own, reloaded.Mode);
            Assert.Equal(WhatsAppProvider.Zenvia, reloaded.Provider);
            Assert.Equal(FromNumber, reloaded.FromNumber);
            Assert.Equal(TemplateId, reloaded.TemplateId);
            Assert.Equal(2, reloaded.Version);
            Assert.Equal(Now, reloaded.ApiTokenUpdatedAt);
            Assert.NotNull(reloaded.ApiToken);
            Assert.Equal("test", reloaded.ApiToken.KeyId);
            Assert.Equal(SentinelApiKey, protector.Unprotect(tenantId, reloaded.ApiToken));
        }

        Assert.Equal("Own", await ScalarAsync<string>(
            database.GetConnectionString(),
            $"SELECT mode FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'"));
    }

    // Criterio 6: la columna guardada no tiene la key en claro.
    [Fact]
    public async Task TheStoredCiphertextDoesNotContainTheKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<IWhatsAppSecretProtector>();
            var settings = TenantWhatsAppSettings.CreateEmpty(tenantId, Now);
            settings.Configure(
                WhatsAppMode.Own, WhatsAppProvider.Zenvia, protector.Protect(tenantId, SentinelApiKey),
                null, FromNumber, TemplateId, Now);
            scope.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>().Add(settings);
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var stored = await ScalarAsync<byte[]>(
            database.GetConnectionString(),
            $"SELECT api_token_ciphertext FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'");

        Assert.True(stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(SentinelApiKey)) < 0);
    }

    // Sin fila, los dos primeros PUT viajan con If-Match "1" y los dos intentan INSERT.
    [Fact]
    public async Task TwoFirstSavesForTheSameTenantEndInAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var firstSettings = TenantWhatsAppSettings.CreateEmpty(tenantId, Now);
        var secondSettings = TenantWhatsAppSettings.CreateEmpty(tenantId, Now);
        firstSettings.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Now);
        secondSettings.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Now);
        first.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>().Add(firstSettings);
        second.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>().Add(secondSettings);
        await first.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
    }
}
