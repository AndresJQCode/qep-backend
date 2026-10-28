using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-12 §3.1 y §3.4 contra Postgres: el atajo es único por tenant y la violación del índice sale como
/// <c>messaging.quick_reply.shortcut_taken</c> (por nombre de índice, no sólo por 23505); editar sin cambiar el atajo no choca con su
/// propia fila; la versión vieja es <c>concurrency.conflict</c>; la lista es del tenant y en orden ordinal.</summary>
public sealed class QuickReplyPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<QuickReply> SaveAsync(QepApiFactory factory, Guid tenantId, string shortcut, string body = "Abrimos de 8 a 6.")
    {
        using var scope = factory.Services.CreateScope();
        var quickReply = QuickReply.Create(tenantId, shortcut, body, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IQuickReplyRepository>().Add(quickReply);
        await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        return quickReply;
    }

    [Fact]
    public async Task TheUniqueIndexHasTheNameTheUnitOfWorkLooksFor()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        var definition = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'messaging' AND indexname = 'IX_quick_replies_tenant_shortcut'");

        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("(tenant_id, shortcut)", definition, StringComparison.Ordinal);
    }

    // Review Focus 1: «HORARIOS» se normaliza a «horarios» y choca; otro tenant puede usarlo.
    [Fact]
    public async Task TheShortcutIsUniquePerTenantAndATakenOneIsADomainError()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        await SaveAsync(factory, tenantId, "horarios");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => SaveAsync(factory, tenantId, "HORARIOS"));
        await SaveAsync(factory, Guid.CreateVersion7(), "horarios");

        Assert.Equal(MessagingErrorCodes.QuickReplyShortcutTaken, error.Code);
        Assert.Equal(2L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.quick_replies WHERE shortcut = 'horarios'"));
    }

    // Review Focus 4: el UPDATE con el mismo atajo toca la misma fila; el índice único no lo ve como duplicado.
    [Fact]
    public async Task UpdatingToItsOwnShortcutDoesNotTripTheIndexAndBumpsTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var saved = await SaveAsync(factory, tenantId, "horarios");

        using (var scope = factory.Services.CreateScope())
        {
            var quickReply = await scope.ServiceProvider.GetRequiredService<IQuickReplyRepository>().FindAsync(tenantId, saved.Id, Ct);
            Assert.NotNull(quickReply);
            quickReply.Update("horarios", "Abrimos de 7 a 7.", DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(2L, await ScalarAsync<long>(connectionString, "SELECT version FROM messaging.quick_replies WHERE id = @id", ("id", saved.Id)));
        Assert.Equal("Abrimos de 7 a 7.", await ScalarAsync<string>(connectionString, "SELECT body FROM messaging.quick_replies WHERE id = @id", ("id", saved.Id)));
    }

    // Review Focus 5: otro pod editó entre la lectura y el commit.
    [Fact]
    public async Task AStaleVersionIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var saved = await SaveAsync(factory, tenantId, "horarios");

        using var scope = factory.Services.CreateScope();
        var quickReply = await scope.ServiceProvider.GetRequiredService<IQuickReplyRepository>().FindAsync(tenantId, saved.Id, Ct);
        Assert.NotNull(quickReply);
        await ExecuteAsync(connectionString, "UPDATE messaging.quick_replies SET version = version + 1 WHERE id = @id", ("id", saved.Id));
        quickReply.Update("horarios", "Otro texto.", DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task TheListIsTheTenantsOnlyInOrdinalOrderAndFindIgnoresOtherTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        await SaveAsync(factory, tenantId, "pago");
        await SaveAsync(factory, tenantId, "horarios");
        await SaveAsync(factory, tenantId, "a-domicilio");
        var foreign = await SaveAsync(factory, Guid.CreateVersion7(), "ajeno");

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IQuickReplyRepository>();
        var list = await repository.ListAsync(tenantId, Ct);

        Assert.Equal(["a-domicilio", "horarios", "pago"], list.Select(quickReply => quickReply.Shortcut));
        Assert.Null(await repository.FindAsync(tenantId, foreign.Id, Ct));
    }
}
