using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Audit.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
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

    // Spec 2026-10-15 §4.1 (D10): el proveedor nuevo entra con la migración AddShopifyProvider que amplía el CHECK.
    [Fact]
    public async Task TheProviderKeyCheckAcceptsShopify()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        _ = factory.Services;
        var id = Guid.CreateVersion7();

        await ExecuteAsync(
            database.GetConnectionString(),
            """
            INSERT INTO integrations.connections (id, tenant_id, provider_key, name, status, fields, created_at, created_by, updated_at, version)
            VALUES (@id, @tenantId, 'shopify', 'Tienda', 'Active', '{}'::jsonb, now(), @memberId, now(), 1)
            """,
            ("id", id),
            ("tenantId", Guid.CreateVersion7()),
            ("memberId", Guid.CreateVersion7()));

        Assert.Equal(1L, await ScalarAsync<long>(
            database.GetConnectionString(),
            "SELECT count(*) FROM integrations.connections WHERE id = @id AND provider_key = 'shopify'",
            ("id", id)));
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
                new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = FromNumber, [ZenviaFieldKeys.TemplateId] = IntegrationsApiHarness.TemplateId },
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

    // Spec 2026-11-12 D1: el candado del token de Shopify es por id (conexión o instalación) y vive con la transacción.
    [Fact]
    public async Task TheShopifyTokenLockExcludesASecondTransaction()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var id = Guid.CreateVersion7();
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var mine = first.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>();
        var theirs = second.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>();

        var held = await mine.BeginTransactionAsync(Ct);
        Assert.True(await mine.TryLockShopifyTokenAsync(id, Ct));
        await using (await theirs.BeginTransactionAsync(Ct))
        {
            Assert.False(await theirs.TryLockShopifyTokenAsync(id, Ct));
            Assert.True(await theirs.TryLockShopifyTokenAsync(Guid.CreateVersion7(), Ct));
        }

        await held.DisposeAsync();
        await using (await theirs.BeginTransactionAsync(Ct))
        {
            Assert.True(await theirs.TryLockShopifyTokenAsync(id, Ct));
        }
    }

    // La variante que espera: el segundo queda bloqueado hasta que el primero confirma al guardar.
    [Fact]
    public async Task TheWaitingShopifyTokenLockWaitsUntilTheFirstSaves()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var id = Guid.CreateVersion7();
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var mine = first.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>();
        var theirs = second.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>();

        await using var held = await mine.BeginTransactionAsync(Ct);
        await mine.LockShopifyTokenAsync(id, Ct);
        await using var waiting = await theirs.BeginTransactionAsync(Ct);
        var lockTask = theirs.LockShopifyTokenAsync(id, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.False(lockTask.IsCompleted);

        await mine.SaveChangesAsync(Ct);

        await lockTask.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task TheLockWithoutATransactionThrows()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var scope = factory.Services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.TryLockShopifyTokenAsync(Guid.CreateVersion7(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.LockShopifyTokenAsync(Guid.CreateVersion7(), Ct));
    }

    // Spec 2026-11-12 §6.1: el refresco de la instalación cambia el par y sus vencimientos; la instalación sigue venciendo igual.
    [Fact]
    public async Task StoringARefreshedTokenReplacesThePairOnly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var install = ShopifyInstall.Start(
            Guid.CreateVersion7(), "mi-tienda.myshopify.com", "c3RhdGUtb2YtdGhlLXJlZnJlc2gtdGVzdC0wMTIzNDU2", "https://qep.qcode.co/shopify/callback",
            null, null, now);
        install.MarkExchanged(
            "12345678", "Mi Tienda", "read_orders", new ProtectedSecret("test", [1]), new ProtectedSecret("test", [2]),
            now.AddHours(1), now.AddDays(90), now);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IShopifyInstallRepository>().Add(install);
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IShopifyInstallRepository>();
            var loaded = (await repository.FindAsync(install.Id, Ct))!;
            loaded.ApplyRefreshedToken(new ProtectedSecret("test", [3]), new ProtectedSecret("test", [4]), now.AddHours(2), now.AddDays(91));

            Assert.True(await repository.TryStoreRefreshedTokenAsync(loaded, Ct));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var stored = (await scope.ServiceProvider.GetRequiredService<IShopifyInstallRepository>().FindAsync(install.Id, Ct))!;
            Assert.Equal([3], stored.ProtectedToken);
            Assert.Equal([4], stored.ProtectedRefreshToken);
            Assert.Equal(now.AddHours(2), stored.AccessTokenExpiresAt);
            Assert.Equal(now.AddDays(91), stored.RefreshTokenExpiresAt);
            Assert.Equal(now.AddDays(7), stored.ExpiresAt);
            Assert.Equal("Mi Tienda", stored.ShopName);
        }

        // Una fila que ya no existe (la borró el enlace o el barrido) no se guarda.
        await ExecuteAsync(connectionString, "DELETE FROM integrations.shopify_installs WHERE id = @id", ("id", install.Id));
        using (var scope = factory.Services.CreateScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<IShopifyInstallRepository>().TryStoreRefreshedTokenAsync(install, Ct));
        }
    }
}
