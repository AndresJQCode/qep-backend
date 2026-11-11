using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Modules.Customers.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Candado de referencias a un cliente (spec 2026-11-10 §borrado): quien escribe una referencia lo toma compartido y quien
/// borra el cliente, exclusivo. Contra PostgreSQL real porque lo que se prueba es la espera del advisory lock.
/// La réplica de <c>customers.customers</c> lleva sólo <c>id</c> y <c>tenant_id</c>, las columnas que <c>Exists</c> lee.
/// </summary>
public sealed class CustomerReferenceLockTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OtherTenantId = Guid.CreateVersion7();

    [Fact]
    public void TheKeyIsStableAndScopedByTenant()
    {
        var customer = Guid.CreateVersion7();

        Assert.Equal(
            $"customers.customer-reference:{TenantId:N}:{customer:N}",
            CustomerReferenceLock.KeyFor(TenantId, customer));
        Assert.NotEqual(CustomerReferenceLock.KeyFor(TenantId, customer), CustomerReferenceLock.KeyFor(OtherTenantId, customer));
    }

    [Fact]
    public async Task ASharedHolderMakesTheExclusiveWait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartDatabaseAsync();
        var customer = Guid.CreateVersion7();
        await using var holder = ContextFor(database);
        await using var writer = ContextFor(database);

        await using var holderTransaction = await holder.Database.BeginTransactionAsync(ct);
        await holder.Database.ExecuteSqlAsync(CustomerReferenceLock.Shared(TenantId, customer), ct);
        await using var writerTransaction = await writer.Database.BeginTransactionAsync(ct);

        var exclusive = writer.Database.ExecuteSqlAsync(CustomerReferenceLock.Exclusive(TenantId, customer), ct);
        await Task.Delay(300, ct);
        Assert.False(exclusive.IsCompleted);

        await holderTransaction.CommitAsync(ct);
        await exclusive.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    // El que esperaba el compartido ve, al despertar, el borrado que el exclusivo ya confirmó: por eso Exists corre
    // después de tomar el candado y no antes.
    [Fact]
    public async Task ExistsSeesTheDeletionCommittedWhileWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartDatabaseAsync();
        var customer = Guid.CreateVersion7();
        await ExecuteAsync(database, $"""
            CREATE SCHEMA customers;
            CREATE TABLE customers.customers (id uuid PRIMARY KEY, tenant_id uuid NOT NULL);
            INSERT INTO customers.customers (id, tenant_id) VALUES ('{customer}', '{TenantId}');
            """);
        await using var reader = ContextFor(database);
        await using var deleter = ContextFor(database);

        await using var deleterTransaction = await deleter.Database.BeginTransactionAsync(ct);
        await deleter.Database.ExecuteSqlAsync(CustomerReferenceLock.Exclusive(TenantId, customer), ct);
        await deleter.Database.ExecuteSqlAsync(
            $"DELETE FROM customers.customers WHERE tenant_id = {TenantId} AND id = {customer}", ct);

        await using var readerTransaction = await reader.Database.BeginTransactionAsync(ct);
        var shared = reader.Database.ExecuteSqlAsync(CustomerReferenceLock.Shared(TenantId, customer), ct);
        await Task.Delay(300, ct);
        Assert.False(shared.IsCompleted);

        await deleterTransaction.CommitAsync(ct);
        await shared.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var exists = await reader.Database.SqlQuery<bool>(CustomerReferenceLock.Exists(TenantId, customer))
            .SingleAsync(ct);
        Assert.False(exists);
    }

    private static CustomersDbContext ContextFor(PostgreSqlContainer database) =>
        new(new DbContextOptionsBuilder<CustomersDbContext>().UseNpgsql(database.GetConnectionString()).Options);

    private static async Task ExecuteAsync(PostgreSqlContainer database, string sql)
    {
        await using var context = ContextFor(database);
        await context.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }
}
