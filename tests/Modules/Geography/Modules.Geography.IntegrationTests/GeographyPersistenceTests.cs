using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Audit.Domain;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// La unidad de trabajo de Geography (spec 2026-10-26 §5): traduce el índice único de los sinónimos activos y la
/// concurrencia optimista a los códigos que el mapeo central conoce, y commitea la auditoría propia del módulo en el
/// mismo <c>SaveChanges</c> que el cambio.
/// </summary>
public sealed class GeographyPersistenceTests
{
    private static readonly Guid OperatorId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");

    [Fact]
    public async Task TwoActiveSynonymsForTheSameTextAre409()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await AddSynonymAsync(factory, "Calli");

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() => AddSynonymAsync(factory, "CALLI"));

        Assert.Equal("geography.synonym.already_exists", exception.Code);
    }

    [Fact]
    public async Task ADeletedSynonymFreesTheText()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var first = await AddSynonymAsync(factory, "Calli");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var synonym = await scope.ServiceProvider.GetRequiredService<ICitySynonymRepository>()
                .FindAsync(first, TestContext.Current.CancellationToken);
            Assert.NotNull(synonym);
            synonym.Delete(OperatorId, DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var second = await AddSynonymAsync(factory, "Calli");

        await using var readScope = factory.Services.CreateAsyncScope();
        var synonyms = readScope.ServiceProvider.GetRequiredService<ICitySynonymRepository>();
        var department = await DepartmentAsync(readScope.ServiceProvider.GetRequiredService<GeographyDbContext>());
        var active = await synonyms.FindActiveAsync(department.Id, "CALLI", TestContext.Current.CancellationToken);
        Assert.NotNull(active);
        Assert.Equal(second, active.Id);
        var deleted = await synonyms.FindAsync(first, TestContext.Current.CancellationToken);
        Assert.NotNull(deleted);
        Assert.True(deleted.IsDeleted);
    }

    [Fact]
    public async Task AStaleSynonymVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var id = await AddSynonymAsync(factory, "Calli");
        await using var stale = factory.Services.CreateAsyncScope();
        var staleSynonym = await stale.ServiceProvider.GetRequiredService<ICitySynonymRepository>()
            .FindAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(staleSynonym);
        await using (var winner = factory.Services.CreateAsyncScope())
        {
            var synonym = await winner.ServiceProvider.GetRequiredService<ICitySynonymRepository>()
                .FindAsync(id, TestContext.Current.CancellationToken);
            Assert.NotNull(synonym);
            synonym.Delete(OperatorId, DateTimeOffset.UtcNow);
            await winner.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        staleSynonym.Delete(OperatorId, DateTimeOffset.UtcNow);
        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            stale.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", exception.Code);
    }

    // Commitea con el cambio y se revierte con él: un sinónimo repetido no deja una entrada de auditoría huérfana.
    [Fact]
    public async Task TheAuditCommitsWithTheChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var tenantId = Guid.Parse(TenantId);

        var id = await AddSynonymAsync(factory, "Calli", tenantId);
        await Assert.ThrowsAsync<ResourceConflictException>(() => AddSynonymAsync(factory, "Calli", tenantId));

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var resourceIds = await dbContext.Database
            .SqlQuery<string>($"SELECT resource_id AS \"Value\" FROM audit.entries WHERE source = 'geography' AND tenant_id = {tenantId}")
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([id.Value.ToString()], resourceIds);
    }

    private static async Task<CitySynonymId> AddSynonymAsync(QepApiFactory factory, string text, Guid? auditTenantId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var department = await DepartmentAsync(dbContext);
        var cali = await dbContext.Cities.SingleAsync(
            value => value.DivipolaCode == "76001", TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var synonym = CitySynonym.Create(CitySynonymId.New(), department.Id, text, cali.Id, OperatorId, now);
        scope.ServiceProvider.GetRequiredService<ICitySynonymRepository>().Add(synonym);
        if (auditTenantId is { } tenantId)
        {
            scope.ServiceProvider.GetRequiredService<IGeographyAuditRecorder>().Record(
                tenantId,
                OperatorId,
                AuditActorType.Human,
                "geography.synonym.created",
                "city_synonym",
                synonym.Id.Value.ToString(),
                "[]",
                now);
        }

        await scope.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return synonym.Id;
    }

    private static Task<Department> DepartmentAsync(GeographyDbContext dbContext) =>
        dbContext.Departments.SingleAsync(value => value.DivipolaCode == "76", TestContext.Current.CancellationToken);
}
