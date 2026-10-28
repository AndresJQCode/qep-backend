using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-15 §3.1 contra Postgres: el nombre es único entre los vigentes de un tenant sin distinguir mayúsculas
/// (<c>IX_resolution_reasons_tenant_name</c>, parcial y por expresión), la violación sale como <c>name_taken</c> (409) por nombre de
/// índice, un archivado no bloquea el nombre y los nombres para leer incluyen los archivados.</summary>
public sealed class ResolutionReasonPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ResolutionReason> SaveAsync(QepApiFactory factory, Guid tenantId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var reason = ResolutionReason.Create(Guid.CreateVersion7(), tenantId, name, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IResolutionReasonRepository>().Add(reason);
        await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        return reason;
    }

    [Fact]
    public async Task TheIndexIsUniquePartialAndByLowerName()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        var definition = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'messaging' AND indexname = 'IX_resolution_reasons_tenant_name'");

        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("lower((name)::text)", definition, StringComparison.Ordinal);
        Assert.Contains("WHERE (deleted_at IS NULL)", definition, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM pg_constraint WHERE conname = 'FK_conversations_resolution_reasons' AND confdeltype = 'r'"));
    }

    [Fact]
    public async Task ANameTakenInTheSameTenantIsAConflictButNotInAnotherOrAfterArchiving()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var first = await SaveAsync(factory, tenantId, "Venta cerrada");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => SaveAsync(factory, tenantId, "VENTA CERRADA"));
        await SaveAsync(factory, Guid.CreateVersion7(), "Venta cerrada");
        await ExecuteAsync(connectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now() WHERE id = @id", ("id", first.Id));
        await SaveAsync(factory, tenantId, "Venta cerrada");

        Assert.Equal(MessagingErrorCodes.ResolutionReasonNameTaken, error.Code);
        Assert.Equal(3L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.resolution_reasons"));
    }

    // Review Focus 1: la búsqueda usa lower() de Postgres en los dos lados, la misma expresión del índice.
    [Fact]
    public async Task FindByNameIgnoresCaseWithAccentsAndSkipsArchived()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var atencion = await SaveAsync(factory, tenantId, "Atención");
        var spam = await SaveAsync(factory, tenantId, "Spam");
        await ExecuteAsync(connectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now() WHERE id = @id", ("id", spam.Id));
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IResolutionReasonRepository>();

        var found = await repository.FindActiveByNameAsync(tenantId, "ATENCIÓN", Ct);
        var archived = await repository.FindActiveByNameAsync(tenantId, "spam", Ct);
        var otherTenant = await repository.FindActiveByNameAsync(Guid.CreateVersion7(), "Atención", Ct);

        Assert.Equal(atencion.Id, found?.Id);
        Assert.Null(archived);
        Assert.Null(otherTenant);
        Assert.Equal(1, await repository.CountActiveAsync(tenantId, Ct));
        Assert.Null(await repository.FindActiveAsync(tenantId, spam.Id, Ct));
        Assert.Equal(["Atención"], (await repository.ListActiveAsync(tenantId, Ct)).Select(reason => reason.Name));
    }

    [Fact]
    public async Task NamesForReadingIncludeArchivedAndStayInTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var spam = await SaveAsync(factory, tenantId, "Spam");
        var foreign = await SaveAsync(factory, Guid.CreateVersion7(), "Ajeno");
        await ExecuteAsync(connectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now() WHERE id = @id", ("id", spam.Id));
        using var scope = factory.Services.CreateScope();

        var names = await scope.ServiceProvider.GetRequiredService<IResolutionReasonNames>().FindAsync(tenantId, [spam.Id, foreign.Id], Ct);

        Assert.Equal("Spam", Assert.Single(names, pair => pair.Key == spam.Id).Value);
        Assert.DoesNotContain(foreign.Id, names.Keys);
    }

    [Fact]
    public async Task TheListIsOrderedByNameIgnoringCase()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        await SaveAsync(factory, tenantId, "spam");
        await SaveAsync(factory, tenantId, "Atención");
        await SaveAsync(factory, tenantId, "Venta cerrada");
        using var scope = factory.Services.CreateScope();

        var list = await scope.ServiceProvider.GetRequiredService<IResolutionReasonRepository>().ListActiveAsync(tenantId, Ct);

        Assert.Equal(["Atención", "spam", "Venta cerrada"], list.Select(reason => reason.Name));
    }
}
