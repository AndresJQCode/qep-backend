using BuildingBlocks.Application;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;
using Npgsql;
using StackExchange.Redis;
using static Modules.ApiAccess.IntegrationTests.ApiAccessApiHarness;

namespace Modules.ApiAccess.IntegrationTests;

/// <summary>
/// Spec 2026-11-13 (Valkey), D-8: el toque de <c>last_used_at</c> pasa primero por <c>qep:touch:apikey:{id}</c> (<c>SET NX EX 60</c>).
/// Con la clave puesta, el request no va a Postgres; sin Valkey, cada request hace su <c>UPDATE</c> como hoy (el filtro de
/// <c>TouchInterval</c> del repositorio sigue). Se cuentan las llamadas a <see cref="IApiKeyRepository.TouchLastUsedAsync"/>: cada una es
/// un <c>UPDATE</c> sobre <c>api_access.api_keys</c>.
/// </summary>
public sealed class ApiKeyTouchGateTests
{
    private const string ExternalConnectionsUrl = "/api/v1/external/whatsapp/connections";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSecondRequestInAMinuteDoesNotUpdate()
    {
        await using var valkey = await ValkeyTestServer.StartAsync(Ct);
        await using var database = await StartDatabaseAsync();
        var touches = new TouchCounter();
        using var factory = NewFactory(database.GetConnectionString(), ValkeyTestServer.ConnectionString(valkey), touches, new FakeClock());
        var (_, plaintext) = await SeedKeyAsync(factory, database.GetConnectionString());

        await CallAsync(factory, plaintext);
        await CallAsync(factory, plaintext);

        Assert.NotNull(factory.Services.GetRequiredService<IApiKeyTouchGate>());
        Assert.Equal(1, touches.Count);
    }

    [Fact]
    public async Task AfterSixtySecondsItUpdatesAgain()
    {
        await using var valkey = await ValkeyTestServer.StartAsync(Ct);
        await using var database = await StartDatabaseAsync();
        var touches = new TouchCounter();
        var clock = new FakeClock();
        using var factory = NewFactory(database.GetConnectionString(), ValkeyTestServer.ConnectionString(valkey), touches, clock);
        var (keyId, plaintext) = await SeedKeyAsync(factory, database.GetConnectionString());
        await CallAsync(factory, plaintext);
        var first = await LastUsedAtAsync(database.GetConnectionString(), keyId);

        // El TTL de 60 s, simulado: se borra la clave del timbre y el reloj del host avanza más que TouchInterval.
        await DeleteGateKeyAsync(valkey, keyId);
        clock.UtcNow = clock.UtcNow.AddSeconds(61);
        await CallAsync(factory, plaintext);

        Assert.Equal(2, touches.Count);
        var second = await LastUsedAtAsync(database.GetConnectionString(), keyId);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second > first, $"last_used_at no se movió: {first} → {second}.");
    }

    [Fact]
    public async Task WithValkeyDownTheUpdateRunsAsToday()
    {
        await using var valkey = await ValkeyTestServer.StartAsync(Ct);
        await using var database = await StartDatabaseAsync();
        var touches = new TouchCounter();
        using var factory = NewFactory(database.GetConnectionString(), ValkeyTestServer.ConnectionString(valkey), touches, new FakeClock());
        var (_, plaintext) = await SeedKeyAsync(factory, database.GetConnectionString());
        await valkey.StopAsync(Ct);

        await CallAsync(factory, plaintext);
        await CallAsync(factory, plaintext);

        Assert.Equal(2, touches.Count);
    }

    private static WebApplicationFactory<Program> NewFactory(
        string connectionString, string valkeyConnectionString, TouchCounter touches, FakeClock clock) =>
        new QepApiFactory(connectionString, services =>
        {
            var descriptor = services.Single(service => service.ServiceType == typeof(IApiKeyRepository));
            services.Remove(descriptor);
            services.AddScoped<IApiKeyRepository>(provider => new CountingKeys(
                (IApiKeyRepository)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!), touches));
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        }).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Valkey:Enabled", "true");
            builder.UseSetting("Valkey:Areas:ApiKeyTouch", "true");
            builder.UseSetting("ConnectionStrings:Valkey", valkeyConnectionString);
        });

    private static async Task<(Guid Id, string Plaintext)> SeedKeyAsync(WebApplicationFactory<Program> factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableDeveloperAsync(connectionString, tenant.TenantId);
        var member = await OwnerMembershipIdAsync(connectionString, tenant);
        var material = ApiKeyMaterial.Generate();
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO api_access.api_keys
                (id, tenant_id, name, key_prefix, secret_hash, scopes, created_at, created_by_member_id, version)
            VALUES (@id, @tenantId, 'ERP', @prefix, @hash, ARRAY['whatsapp.templates.send']::text[], now(), @memberId, 1)
            """,
            ("id", id), ("tenantId", tenant.TenantId), ("prefix", material.Prefix), ("hash", material.SecretHash), ("memberId", member));
        return (id, material.Plaintext);
    }

    // El toque pasa en la autenticación: lo que responda el endpoint después (200, 403) no importa acá.
    private static async Task CallAsync(WebApplicationFactory<Program> factory, string plaintext)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", plaintext);
        using var response = await client.GetAsync(ExternalConnectionsUrl, Ct);
        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<DateTimeOffset?> LastUsedAtAsync(string connectionString, Guid keyId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT last_used_at FROM api_access.api_keys WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", keyId);
        return await command.ExecuteScalarAsync(Ct) is DateTime value ? new DateTimeOffset(value, TimeSpan.Zero) : null;
    }

    private static async Task DeleteGateKeyAsync(IContainer valkey, Guid keyId)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(ValkeyTestServer.ConnectionString(valkey));
        Assert.True(await multiplexer.GetDatabase().KeyDeleteAsync($"qep:touch:apikey:{keyId:D}"));
    }

    private sealed class TouchCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class CountingKeys(IApiKeyRepository inner, TouchCounter touches) : IApiKeyRepository
    {
        public void Add(ApiKey key) => inner.Add(key);

        public Task<ApiKey?> FindAsync(Guid tenantId, ApiKeyId id, CancellationToken cancellationToken) =>
            inner.FindAsync(tenantId, id, cancellationToken);

        public Task<IReadOnlyList<ApiKey>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            inner.ListAsync(tenantId, cancellationToken);

        public Task<int> CountActiveAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.CountActiveAsync(tenantId, now, cancellationToken);

        public Task<ApiKey?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken) =>
            inner.FindByPrefixAsync(keyPrefix, cancellationToken);

        public Task TouchLastUsedAsync(ApiKeyId id, DateTimeOffset now, CancellationToken cancellationToken)
        {
            touches.Add();
            return inner.TouchLastUsedAsync(id, now, cancellationToken);
        }
    }
}
