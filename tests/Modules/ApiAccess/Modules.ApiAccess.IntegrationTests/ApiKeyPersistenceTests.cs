using Microsoft.Extensions.DependencyInjection;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;
using Npgsql;

namespace Modules.ApiAccess.IntegrationTests;

public sealed class ApiKeyPersistenceTests
{
    private static readonly string[] Scopes = ["whatsapp.templates.send"];

    private static ApiKey NewKey(
        Guid tenantId, Guid memberId, string name, ApiKeyMaterial? material = null) =>
        ApiKey.Create(
            tenantId,
            name,
            material ?? ApiKeyMaterial.Generate(),
            Scopes,
            connectionIds: null,
            expiresAt: null,
            memberId,
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task TheHashIsStoredAndNeverThePlaintext()
    {
        var (database, factory) = await ApiAccessApiHarness.OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var connectionString = database.GetConnectionString();
        var tenant = await ApiAccessApiHarness.RegisterTenantAsync(factory);
        var memberId = await ApiAccessApiHarness.OwnerMembershipIdAsync(connectionString, tenant);
        var material = ApiKeyMaterial.Generate();

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IApiKeyRepository>()
                .Add(NewKey(tenant.TenantId, memberId, "ERP", material));
            await scope.ServiceProvider.GetRequiredService<IApiAccessUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var storedHash = await ApiAccessApiHarness.ScalarAsync<byte[]>(
            connectionString,
            "SELECT secret_hash FROM api_access.api_keys WHERE key_prefix = @prefix",
            ("prefix", material.Prefix));
        Assert.Equal(material.SecretHash, storedHash);

        // Ninguna columna de texto, ni siquiera por casualidad, lleva el secreto o la clave completa.
        var leaks = await ApiAccessApiHarness.ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM api_access.api_keys WHERE to_jsonb(api_keys)::text LIKE '%' || @secret || '%'",
            ("secret", material.Secret));
        Assert.Equal(0, leaks);

        var found = await FindByPrefixAsync(factory, material.Prefix);
        Assert.NotNull(found);
        Assert.Equal(tenant.TenantId, found.TenantId);
    }

    [Fact]
    public async Task TwoActiveKeysCannotShareANameButARevokedOneFreesIt()
    {
        var (database, factory) = await ApiAccessApiHarness.OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var connectionString = database.GetConnectionString();
        var tenant = await ApiAccessApiHarness.RegisterTenantAsync(factory);
        var memberId = await ApiAccessApiHarness.OwnerMembershipIdAsync(connectionString, tenant);

        await SaveAsync(factory, NewKey(tenant.TenantId, memberId, "ERP"));

        var taken = await Assert.ThrowsAsync<ApiAccessDomainException>(
            () => SaveAsync(factory, NewKey(tenant.TenantId, memberId, "ERP")));
        Assert.Equal(ApiAccessErrorCodes.KeyNameTaken, taken.Code);

        // Revocar libera el nombre.
        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
            var first = (await repository.ListAsync(
                tenant.TenantId, TestContext.Current.CancellationToken)).Single();
            var tracked = await repository.FindAsync(
                tenant.TenantId, first.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(tracked);
            Assert.True(tracked.Revoke(memberId, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IApiAccessUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SaveAsync(factory, NewKey(tenant.TenantId, memberId, "ERP"));

        using var check = factory.Services.CreateScope();
        var all = await check.ServiceProvider.GetRequiredService<IApiKeyRepository>()
            .ListAsync(tenant.TenantId, TestContext.Current.CancellationToken);
        Assert.Equal(2, all.Count);
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<IApiKeyRepository>()
            .CountActiveAsync(tenant.TenantId, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheDatabaseRejectsAnEmptyConnectionListAndAnEmptyScopeList()
    {
        var (database, factory) = await ApiAccessApiHarness.OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var connectionString = database.GetConnectionString();
        var tenant = await ApiAccessApiHarness.RegisterTenantAsync(factory);
        var memberId = await ApiAccessApiHarness.OwnerMembershipIdAsync(connectionString, tenant);

        const string Insert =
            """
            INSERT INTO api_access.api_keys
                (id, tenant_id, name, key_prefix, secret_hash, scopes, connection_ids, created_at, created_by_member_id, version)
            VALUES
                (@id, @tenantId, @name, @prefix, @hash, @scopes, @connections, now(), @memberId, 1)
            """;

        async Task<PostgresException> InsertAsync(string name, string prefix, string[] scopes, Guid[]? connections)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(Insert, connection);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("tenantId", tenant.TenantId);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("prefix", prefix);
            command.Parameters.AddWithValue("hash", new byte[32]);
            command.Parameters.AddWithValue("scopes", scopes);
            command.Parameters.Add(new NpgsqlParameter("connections", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            {
                Value = (object?)connections ?? DBNull.Value,
            });
            command.Parameters.AddWithValue("memberId", memberId);
            return await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var emptyConnections = await InsertAsync("a", "AAAAAAAA", Scopes, []);
        Assert.Equal(PostgresErrorCodes.CheckViolation, emptyConnections.SqlState);
        Assert.Equal("CK_api_keys_connections", emptyConnections.ConstraintName);

        var emptyScopes = await InsertAsync("b", "BBBBBBBB", [], null);
        Assert.Equal(PostgresErrorCodes.CheckViolation, emptyScopes.SqlState);
        Assert.Equal("CK_api_keys_scopes", emptyScopes.ConstraintName);
    }

    [Fact]
    public async Task TouchWritesOnceAMinuteAtMost()
    {
        var (database, factory) = await ApiAccessApiHarness.OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var connectionString = database.GetConnectionString();
        var tenant = await ApiAccessApiHarness.RegisterTenantAsync(factory);
        var memberId = await ApiAccessApiHarness.OwnerMembershipIdAsync(connectionString, tenant);
        var key = NewKey(tenant.TenantId, memberId, "ERP");
        await SaveAsync(factory, key);

        var t0 = DateTimeOffset.UtcNow;

        async Task<DateTimeOffset?> TouchAsync(DateTimeOffset now)
        {
            using var scope = factory.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
            await repository.TouchLastUsedAsync(key.Id, now, TestContext.Current.CancellationToken);
            return (await repository.FindByPrefixAsync(
                key.KeyPrefix, TestContext.Current.CancellationToken))?.LastUsedAt;
        }

        Assert.Equal(t0, (await TouchAsync(t0))!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(t0, (await TouchAsync(t0.AddSeconds(59)))!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(
            t0.AddSeconds(61), (await TouchAsync(t0.AddSeconds(61)))!.Value, TimeSpan.FromMilliseconds(1));
    }

    private static async Task SaveAsync(ApiAccessApiHarness.QepApiFactory factory, ApiKey key)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IApiKeyRepository>().Add(key);
        await scope.ServiceProvider.GetRequiredService<IApiAccessUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ApiKey?> FindByPrefixAsync(
        ApiAccessApiHarness.QepApiFactory factory, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IApiKeyRepository>()
            .FindByPrefixAsync(prefix, TestContext.Current.CancellationToken);
    }
}
