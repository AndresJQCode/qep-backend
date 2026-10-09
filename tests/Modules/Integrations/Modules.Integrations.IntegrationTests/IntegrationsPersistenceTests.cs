using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Audit.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Npgsql;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Lo que sólo la base hace cumplir (spec 2026-10-08, «Conexión»): los CHECK, el índice único del
/// nombre traducido por su nombre, la concurrencia sobre la versión, la cascada de los secretos, y
/// auditoría y outbox en la misma transacción.
/// </summary>
public sealed class IntegrationsPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARoundTripKeepsNameFieldsSecretStatusAndVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var id = await SeedConnectionAsync(factory, tenantId, "WhatsApp Norte");

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>();
        var connection = await repository.FindAsync(tenantId, id, Ct);

        Assert.NotNull(connection);
        Assert.Equal("WhatsApp Norte", connection.Name);
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(1, connection.Version);
        Assert.Equal(FromNumber, connection.Fields[ZenviaFieldKeys.FromNumber]);
        var secret = Assert.Single(connection.Secrets);
        Assert.Equal("test", secret.KeyId);
        Assert.True(scope.ServiceProvider.GetRequiredService<ISecretProtector>()
            .TryUnprotect(id, secret.FieldKey, secret.Protected, out var plaintext));
        Assert.Equal(SentinelApiToken, plaintext);
        Assert.Null(await repository.FindAsync(Guid.CreateVersion7(), id, Ct));
    }

    // D9 y Review Focus 2: el token nunca está en fields, y lo cifrado no lo contiene.
    [Fact]
    public async Task TheSecretIsNeverInFieldsNorReadableInTheCiphertext()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var id = await SeedConnectionAsync(factory, Guid.CreateVersion7(), "Norte");

        var fields = await ScalarAsync<string>(connectionString, "SELECT fields::text FROM integrations.connections WHERE id = @id", ("id", id));
        var clean = await ScalarAsync<bool>(
            connectionString,
            "SELECT position(convert_to(@token, 'UTF8') in ciphertext) = 0 FROM integrations.connection_secrets WHERE connection_id = @id",
            ("token", SentinelApiToken),
            ("id", id));

        Assert.Contains(FromNumber, fields, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelApiToken, fields, StringComparison.Ordinal);
        Assert.True(clean);
    }

    [Fact]
    public async Task TheProviderKeyCheckRejectsAKeyOutsideTheCatalog()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        _ = factory.Services;

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.GetConnectionString(),
            """
            INSERT INTO integrations.connections (id, tenant_id, provider_key, name, status, fields, created_at, created_by, updated_at, version)
            VALUES (@id, @tenantId, 'otro', 'x', 'Active', '{}'::jsonb, now(), @memberId, now(), 1)
            """,
            ("id", Guid.CreateVersion7()),
            ("tenantId", Guid.CreateVersion7()),
            ("memberId", Guid.CreateVersion7())));

        Assert.Equal("CK_connections_provider_key", error.ConstraintName);
    }

    [Fact]
    public async Task TheStatusCheckRejectsAnUnknownStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var id = await SeedConnectionAsync(factory, Guid.CreateVersion7(), "Norte");

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE integrations.connections SET status = 'Broken' WHERE id = @id",
            ("id", id)));

        Assert.Equal("CK_connections_status", error.ConstraintName);
    }

    // Review Focus 5: único por (tenant, proveedor, lower(name)); el dominio recorta los espacios.
    [Fact]
    public async Task ANameRepeatedWithOtherCaseOrSpacesIsNameTakenOnlyInsideTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        await SeedConnectionAsync(factory, tenantId, "WhatsApp Norte");

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            SeedConnectionAsync(factory, tenantId, "  whatsapp norte  "));

        Assert.Equal("integrations.connection.name_taken", error.Code);
        Assert.NotEqual(Guid.Empty, await SeedConnectionAsync(factory, Guid.CreateVersion7(), "WhatsApp Norte"));
    }

    [Fact]
    public async Task TwoWritersOnTheSameVersionCollide()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var id = await SeedConnectionAsync(factory, tenantId, "Norte");
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var mine = await first.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().FindAsync(tenantId, id, Ct);
        var theirs = await second.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().FindAsync(tenantId, id, Ct);

        mine!.Pause(DateTimeOffset.UtcNow);
        await first.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        theirs!.Pause(DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task DeletingAConnectionCascadesItsSecrets()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var id = await SeedConnectionAsync(factory, tenantId, "Norte");

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>();
            repository.Remove((await repository.FindAsync(tenantId, id, Ct))!);
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM integrations.connection_secrets WHERE connection_id = @id", ("id", id)));
    }

    [Fact]
    public async Task AuditAndEventsCommitWithTheConnectionInOneSave()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        Guid id;

        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var now = DateTimeOffset.UtcNow;
            var connection = IntegrationConnection.Create(
                IntegrationProviders.Zenvia,
                tenantId,
                "Norte",
                new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = FromNumber },
                new Dictionary<string, string> { [ZenviaFieldKeys.ApiToken] = SentinelApiToken },
                services.GetRequiredService<ISecretProtector>().Protect,
                Guid.CreateVersion7(),
                now);
            id = connection.Id;
            services.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
            services.GetRequiredService<IIntegrationsAuditRecorder>().Record(
                tenantId, Guid.CreateVersion7(), AuditActorType.Human, ConnectionAuditActions.Created, connection.Id,
                ConnectionAuditActions.Success, ["apiToken", "fromNumber", "name"], now);
            services.GetRequiredService<IConnectionEventPublisher>().Publish(ConnectionEvents.Paused, connection, now);
            await services.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_type = 'integration_connection' AND source = 'integrations' AND resource_id = @id",
            ("id", id.ToString())));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'integrations.connection-paused.v1' AND payload->>'connectionId' = @id AND payload->>'providerKey' = 'zenvia'",
            ("id", id.ToString())));
    }
}
