using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.2, §8.3 y §9.4 contra la base: crear el incompleto (con auditoría), vincular por
/// teléfono sin pisar otro BSUID (D-A6), la carrera de dos entregas del mismo BSUID (Review Focus RF9), el
/// reemplazo de BSUID y las lecturas por id y por nombre.</summary>
public sealed class CustomerWhatsAppDirectoryTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);

    private static async Task<T> InScopeAsync<T>(QepApiFactory factory, Func<ICustomerWhatsAppDirectory, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>());
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task ANewBsuidCreatesAnIncompleteCustomerOnceAndAuditsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var contact = new WhatsAppContact("CO.1349120865530274", "+573001234567", "Laura Pérez", "laura.p");

        var first = await InScopeAsync(factory, directory => directory.EnsureAsync(Tenant, contact, TestContext.Current.CancellationToken));
        var again = await InScopeAsync(factory, directory => directory.EnsureAsync(Tenant, contact, TestContext.Current.CancellationToken));

        Assert.Equal(EnsureOutcome.Created, first.Outcome);
        Assert.Equal(new EnsuredCustomer(first.CustomerId, EnsureOutcome.Existing), again);
        Assert.Equal("Incomplete|Laura Pérez|CO|+573001234567|CO.1349120865530274", await ScalarAsync(database.GetConnectionString(),
            $"SELECT completeness || '|' || name || '|' || country || '|' || phone_e164 || '|' || whatsapp_user_id FROM customers.customers WHERE id = '{first.CustomerId}'"));
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(),
            $"SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = '{CustomerAuditActions.CreatedFromMessaging}' AND payload->>'actorId' = '{Guid.Empty}'"));
    }

    [Fact]
    public async Task AnExistingPhoneIsLinkedAndKeepsItsFirstBsuid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var existing = await CreateCustomerAsync(client, city.CityId, classification.Id); // teléfono 310 935 2187 → +573109352187

        var linked = await InScopeAsync(factory, directory => directory.EnsureAsync(
            Tenant, new WhatsAppContact("CO.AAA", "+573109352187", "Otra", null), TestContext.Current.CancellationToken));
        var otherPortfolio = await InScopeAsync(factory, directory => directory.EnsureAsync(
            Tenant, new WhatsAppContact("CO.BBB", "+573109352187", "Otra", null), TestContext.Current.CancellationToken));

        Assert.Equal(new EnsuredCustomer(existing.Id, EnsureOutcome.Linked), linked);
        Assert.Equal(new EnsuredCustomer(existing.Id, EnsureOutcome.Linked), otherPortfolio);
        Assert.Equal("CO.AAA|Complete", await ScalarAsync(database.GetConnectionString(),
            $"SELECT whatsapp_user_id || '|' || completeness FROM customers.customers WHERE id = '{existing.Id}'"));
    }

    // RF9: la carrera la arbitra IX_customers_tenant_whatsapp_user_id; el perdedor relee.
    [Fact]
    public async Task ConcurrentEnsuresOfTheSameBsuidCreateOneCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var contact = new WhatsAppContact("CO.RACE", null, "Laura", null);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            InScopeAsync(factory, directory => directory.EnsureAsync(Tenant, contact, TestContext.Current.CancellationToken))));

        Assert.Single(results.Select(result => result.CustomerId).Distinct());
        Assert.Single(results, result => result.Outcome == EnsureOutcome.Created);
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(), "SELECT count(*) FROM customers.customers WHERE whatsapp_user_id = 'CO.RACE'"));
    }

    // §9.4 (P3) contra la migración real: el nombre del índice se traduce y el tracker queda limpio, así que el
    // siguiente SaveChanges del mismo scope no reintenta la fila que perdió.
    [Fact]
    public async Task ALosingInsertIsTranslatedAndLeavesTheScopeClean()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        _ = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.X", null, "Laura", null), TestContext.Current.CancellationToken));

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>();
        repository.Add(Customer.CreateIncomplete(
            CustomerId.New(), Tenant, "Otra", null, null, "CO.X", DateTimeOffset.UtcNow,
            scope.ServiceProvider.GetRequiredService<IPhoneNumberNormalizer>()));

        await Assert.ThrowsAsync<WhatsAppUserIdTakenException>(() => unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // D-A6 y §8.3 con UPDATE condicional: el primer BSUID se queda (sin subir la versión, P4), el reemplazo exige
    // el anterior, y chocar con el índice se traduce igual que en SaveChanges.
    [Fact]
    public async Task AttachAndReplaceAreConditionalAndTranslated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var first = await CreateCustomerAsync(client, city.CityId, classification.Id);
        var second = await CreateCustomerAsync(client, city.CityId, classification.Id, name: "Otra S.A.S.", identificationNumber: "900.555.444-1");
        _ = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.X", null, "Laura", null), TestContext.Current.CancellationToken));
        var versionBefore = await ScalarAsync(database.GetConnectionString(), $"SELECT version FROM customers.customers WHERE id = '{first.Id}'");

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await repository.TryAttachWhatsAppUserIdAsync(Tenant, new CustomerId(first.Id), "CO.A", ct));
        Assert.False(await repository.TryAttachWhatsAppUserIdAsync(Tenant, new CustomerId(first.Id), "CO.B", ct));
        await Assert.ThrowsAsync<WhatsAppUserIdTakenException>(() => repository.TryAttachWhatsAppUserIdAsync(Tenant, new CustomerId(second.Id), "CO.X", ct));
        Assert.Equal($"CO.A|{versionBefore}", await ScalarAsync(database.GetConnectionString(),
            $"SELECT whatsapp_user_id || '|' || version FROM customers.customers WHERE id = '{first.Id}'"));

        Assert.False(await repository.TryReplaceWhatsAppUserIdAsync(Tenant, "CO.NOPE", "CO.Z", ct));
        await Assert.ThrowsAsync<WhatsAppUserIdTakenException>(() => repository.TryReplaceWhatsAppUserIdAsync(Tenant, "CO.A", "CO.X", ct));
        Assert.True(await repository.TryReplaceWhatsAppUserIdAsync(Tenant, "CO.A", "CO.C", ct));
        Assert.Equal("CO.C", await ScalarAsync(database.GetConnectionString(), $"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{first.Id}'"));
    }

    [Fact]
    public async Task ReplacingMovesTheBsuidUnlessTheNewOneBelongsToAnotherCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var laura = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.OLD", null, "Laura", null), TestContext.Current.CancellationToken));
        _ = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.TAKEN", null, "Pedro", null), TestContext.Current.CancellationToken));

        Assert.True(await InScopeAsync(factory, d => d.ReplaceWhatsAppUserIdAsync(Tenant, "CO.OLD", "CO.NEW", TestContext.Current.CancellationToken)));
        Assert.False(await InScopeAsync(factory, d => d.ReplaceWhatsAppUserIdAsync(Tenant, "CO.OLD", "CO.NEW", TestContext.Current.CancellationToken)));
        Assert.False(await InScopeAsync(factory, d => d.ReplaceWhatsAppUserIdAsync(Tenant, "CO.NEW", "CO.TAKEN", TestContext.Current.CancellationToken)));
        Assert.Equal("CO.NEW", await ScalarAsync(database.GetConnectionString(), $"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{laura.CustomerId}'"));
    }

    // Review Focus 3 (spec 2026-10-24 §3.3): el que queda ya tenía BSUID, así que el del fusionado se quedó en el fusionado.
    // El mensaje que vuelve a llegar por ese BSUID cae en el que quedó y no nace un incompleto nuevo.
    [Fact]
    public async Task AnInboundWithTheBsuidLeftOnTheMergedLandsOnTheSurvivor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var ct = TestContext.Current.CancellationToken;
        var survivor = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.SURVIVOR", null, "Laura", null), ct));
        var merged = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.MERGED", null, "Laura P.", null), ct));
        await MarkMergedAsync(database.GetConnectionString(), merged.CustomerId, survivor.CustomerId);

        var again = await InScopeAsync(factory, d => d.EnsureAsync(
            Tenant, new WhatsAppContact("CO.MERGED", "+573001234567", "Laura P.", null), ct));

        Assert.Equal(new EnsuredCustomer(survivor.CustomerId, EnsureOutcome.Existing), again);
        Assert.Equal("2", await ScalarAsync(database.GetConnectionString(), $"SELECT count(*) FROM customers.customers WHERE tenant_id = '{Tenant}'"));
    }

    // §3.3: el teléfono sólo lo tiene el fusionado. Se vincula al que quedó (el BSUID nuevo va a él) y no al fusionado, al que
    // TryAttachWhatsAppUserIdAsync ya no le escribe (desvío F4).
    [Fact]
    public async Task APhoneOnlyTheMergedHasResolvesToTheSurvivorAndAttachesThere()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var survivor = await CreateCustomerAsync(client, city.CityId, classification.Id); // teléfono +573109352187
        var ct = TestContext.Current.CancellationToken;
        var merged = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.MERGED", "+573001112233", "Laura", null), ct));
        await MarkMergedAsync(database.GetConnectionString(), merged.CustomerId, survivor.Id);

        var linked = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.NEW", "+573001112233", "Laura", null), ct));

        Assert.Equal(new EnsuredCustomer(survivor.Id, EnsureOutcome.Linked), linked);
        Assert.Equal("CO.NEW", await ScalarAsync(database.GetConnectionString(), $"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{survivor.Id}'"));
        Assert.Equal("CO.MERGED", await ScalarAsync(database.GetConnectionString(), $"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{merged.CustomerId}'"));
        Assert.Equal("2", await ScalarAsync(database.GetConnectionString(), $"SELECT count(*) FROM customers.customers WHERE tenant_id = '{Tenant}'"));
    }

    // §3.3, el espejo de Instagram: el IGSID que se quedó en el fusionado resuelve al que quedó.
    [Fact]
    public async Task AnIgsidLeftOnTheMergedLandsOnTheSurvivor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var ct = TestContext.Current.CancellationToken;

        async Task<EnsuredCustomer> EnsureInstagramAsync(InstagramContact contact)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICustomerInstagramDirectory>().EnsureAsync(Tenant, contact, ct);
        }

        var survivor = await EnsureInstagramAsync(new InstagramContact("17841400000000001", "laura", "Laura"));
        var merged = await EnsureInstagramAsync(new InstagramContact("17841400000000002", "laura.p", "Laura P."));
        await MarkMergedAsync(database.GetConnectionString(), merged.CustomerId, survivor.CustomerId);

        var again = await EnsureInstagramAsync(new InstagramContact("17841400000000002", "laura.p", "Laura P."));

        Assert.Equal(new EnsuredCustomer(survivor.CustomerId, EnsureOutcome.Existing), again);
        Assert.Equal("2", await ScalarAsync(database.GetConnectionString(), $"SELECT count(*) FROM customers.customers WHERE tenant_id = '{Tenant}'"));
    }

    [Fact]
    public async Task RefsAndIdsByNameReadOnlyThisTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var laura = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.L", null, "Laura Pérez", null), TestContext.Current.CancellationToken));

        var refs = await InScopeAsync(factory, d => d.FindRefsAsync(Tenant, [laura.CustomerId, Guid.CreateVersion7()], TestContext.Current.CancellationToken));
        var ids = await InScopeAsync(factory, d => d.FindIdsByNameAsync(Tenant, "pére", 200, TestContext.Current.CancellationToken));
        var foreign = await InScopeAsync(factory, d => d.FindIdsByNameAsync(Guid.CreateVersion7(), "pére", 200, TestContext.Current.CancellationToken));

        Assert.Equal(new CustomerWhatsAppRef(laura.CustomerId, "Laura Pérez", false), Assert.Single(refs).Value);
        Assert.Equal(laura.CustomerId, Assert.Single(ids));
        Assert.Empty(foreign);
    }

    [Fact]
    public async Task AnUnknownPhoneCreatesAnIncompleteCustomerWithoutBsuid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);

        var ensured = await InScopeAsync(factory, d => d.EnsureByPhoneAsync(
            Tenant, new WhatsAppPhoneContact("+573001112233", "Ana Pérez"), TestContext.Current.CancellationToken));

        Assert.Equal(EnsureOutcome.Created, ensured.Outcome);
        Assert.Equal("Incomplete|+573001112233|NULL|Ana Pérez", await ScalarAsync(database.GetConnectionString(),
            $"SELECT completeness || '|' || phone_e164 || '|' || coalesce(whatsapp_user_id, 'NULL') || '|' || name FROM customers.customers WHERE id = '{ensured.CustomerId}'"));
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(),
            $"SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = '{CustomerAuditActions.CreatedFromMessaging}' AND payload->>'actorId' = '{Guid.Empty}'"));
    }

    [Fact]
    public async Task AKnownPhoneIsNotDuplicated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var existing = await CreateCustomerAsync(client, city.CityId, classification.Id); // +573109352187

        var ensured = await InScopeAsync(factory, d => d.EnsureByPhoneAsync(
            Tenant, new WhatsAppPhoneContact("+573109352187", "Otro nombre"), TestContext.Current.CancellationToken));

        Assert.Equal(new EnsuredCustomer(existing.Id, EnsureOutcome.Existing), ensured);
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(),
            "SELECT count(*) FROM customers.customers WHERE phone_e164 = '+573109352187'"));
    }

    [Fact]
    public async Task ALaterLiveMessageAttachesTheBsuidToThePhoneCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var byPhone = await InScopeAsync(factory, d => d.EnsureByPhoneAsync(
            Tenant, new WhatsAppPhoneContact("+573001112233", "Ana"), TestContext.Current.CancellationToken));

        var live = await InScopeAsync(factory, d => d.EnsureAsync(
            Tenant, new WhatsAppContact("CO.123", "+573001112233", "Ana", null), TestContext.Current.CancellationToken));

        Assert.Equal(new EnsuredCustomer(byPhone.CustomerId, EnsureOutcome.Linked), live);
        Assert.Equal("CO.123", await ScalarAsync(database.GetConnectionString(),
            $"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{byPhone.CustomerId}'"));
    }

    // P-7: sólo el nombre que es el respaldo del teléfono se reemplaza; el de una persona (o de una ficha completa) se respeta.
    [Fact]
    public async Task AContactNameReplacesOnlyThePhoneFallbackName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var complete = await CreateCustomerAsync(client, city.CityId, classification.Id); // +573109352187, completo
        var ct = TestContext.Current.CancellationToken;
        var fallbackNamed = await InScopeAsync(factory, d => d.EnsureByPhoneAsync(Tenant, new WhatsAppPhoneContact("+573001112233", null), ct));
        var handNamed = await InScopeAsync(factory, d => d.EnsureByPhoneAsync(Tenant, new WhatsAppPhoneContact("+573004445566", "Nombre propio"), ct));

        _ = await InScopeAsync(factory, d => d.EnsureByPhoneAsync(Tenant, new WhatsAppPhoneContact("+573001112233", "Ana Pérez"), ct));
        _ = await InScopeAsync(factory, d => d.EnsureByPhoneAsync(Tenant, new WhatsAppPhoneContact("+573004445566", "Ana Pérez"), ct));
        _ = await InScopeAsync(factory, d => d.EnsureByPhoneAsync(Tenant, new WhatsAppPhoneContact("+573109352187", "Ana Pérez"), ct));

        Assert.Equal("Ana Pérez", await ScalarAsync(database.GetConnectionString(), $"SELECT name FROM customers.customers WHERE id = '{fallbackNamed.CustomerId}'"));
        Assert.Equal("Nombre propio", await ScalarAsync(database.GetConnectionString(), $"SELECT name FROM customers.customers WHERE id = '{handNamed.CustomerId}'"));
        Assert.Equal("Verde Esencial S.A.S.", await ScalarAsync(database.GetConnectionString(), $"SELECT name FROM customers.customers WHERE id = '{complete.Id}'"));
    }
}
