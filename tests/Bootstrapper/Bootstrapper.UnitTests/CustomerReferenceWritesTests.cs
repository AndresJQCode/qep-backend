using System.Data;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Bootstrapper.UnitTests;

/// <summary>
/// <see cref="CustomerReferenceWrites"/>: el lado del escritor del candado de referencias a un cliente, compartido por Quotations,
/// Loans, ServiceContracts y Messaging (final review R8). <c>HoldAsync</c> contra PostgreSQL real, porque lo que importa es la
/// transacción; <c>Collect</c> sólo mira el ChangeTracker y no abre conexión.
/// </summary>
public sealed class CustomerReferenceWritesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HoldingWithoutATransactionIsRefused()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = TrackingContext.For(database.GetConnectionString());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CustomerReferenceWrites.HoldAsync(context.Database, TenantId, Guid.CreateVersion7(), Ct));
    }

    // En REPEATABLE READ la foto se toma antes de esperar el candado: Exists vería al cliente que el borrado ya se llevó.
    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task HoldingInsideASnapshotTransactionIsRefused(IsolationLevel isolation)
    {
        await using var database = await StartDatabaseAsync();
        await using var context = TrackingContext.For(database.GetConnectionString());
        await using var transaction = await context.Database.BeginTransactionAsync(isolation, Ct);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CustomerReferenceWrites.HoldAsync(context.Database, TenantId, Guid.CreateVersion7(), Ct));
        Assert.Contains("READ COMMITTED", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoldingInReadCommittedReportsWhetherTheCustomerExists()
    {
        await using var database = await StartDatabaseAsync();
        var customer = Guid.CreateVersion7();
        await using var context = TrackingContext.For(database.GetConnectionString());
        var seed = $"""
            CREATE SCHEMA customers;
            CREATE TABLE customers.customers (id uuid PRIMARY KEY, tenant_id uuid NOT NULL);
            INSERT INTO customers.customers (id, tenant_id) VALUES ('{customer}', '{TenantId}');
            """;
        await context.Database.ExecuteSqlRawAsync(seed, Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        Assert.True(await CustomerReferenceWrites.HoldAsync(context.Database, TenantId, customer, Ct));
        Assert.False(await CustomerReferenceWrites.HoldAsync(context.Database, TenantId, Guid.CreateVersion7(), Ct));
    }

    // La prioridad reemplaza el olfateo de prefijos de código: si el mismo cliente sale en dos fuentes, gana la de prioridad más alta.
    [Fact]
    public void CollectingKeepsTheHighestPriorityCodeAndSortsTheCustomers()
    {
        using var context = TrackingContext.For("Host=unused");
        var shared = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        context.Add(new Low { Id = Guid.CreateVersion7(), TenantId = TenantId, CustomerId = shared });
        context.Add(new High { Id = Guid.CreateVersion7(), TenantId = TenantId, ClientId = shared });
        context.Add(new Low { Id = Guid.CreateVersion7(), TenantId = TenantId, CustomerId = other });

        var references = CustomerReferenceWrites.Collect(context.ChangeTracker, Sources);

        Assert.Equal(
            new[] { new CustomerReferenceToHold(TenantId, shared, "high"), new CustomerReferenceToHold(TenantId, other, "low") }
                .OrderBy(reference => reference.CustomerId),
            references);
    }

    // Lo que no escribe una referencia nueva no se toma: sin cliente (vacío o null), sin cambio de cliente, o una entidad ajena.
    [Fact]
    public void CollectingSkipsWhatDoesNotWriteANewReference()
    {
        using var context = TrackingContext.For("Host=unused");
        context.Add(new Low { Id = Guid.CreateVersion7(), TenantId = TenantId, CustomerId = Guid.Empty });
        context.Add(new High { Id = Guid.CreateVersion7(), TenantId = TenantId, ClientId = null });
        context.Add(new Unrelated { Id = Guid.CreateVersion7(), TenantId = TenantId, CustomerId = Guid.CreateVersion7() });
        var unchanged = new Low { Id = Guid.CreateVersion7(), TenantId = TenantId, CustomerId = Guid.CreateVersion7(), Note = "a" };
        context.Attach(unchanged);
        unchanged.Note = "b";
        var moved = new Low { Id = Guid.CreateVersion7(), TenantId = TenantId, CustomerId = Guid.CreateVersion7() };
        context.Attach(moved);
        var target = Guid.CreateVersion7();
        moved.CustomerId = target;
        context.ChangeTracker.DetectChanges();

        var references = CustomerReferenceWrites.Collect(context.ChangeTracker, Sources);

        Assert.Equal([new CustomerReferenceToHold(TenantId, target, "low")], references);
    }

    private static readonly CustomerReferenceSource[] Sources =
    [
        new(typeof(Low), nameof(Low.CustomerId), "low"),
        new(typeof(High), nameof(High.ClientId), "high", Priority: 1),
    ];

    private sealed class Low
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid CustomerId { get; set; }

        public string Note { get; set; } = string.Empty;
    }

    private sealed class High
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid? ClientId { get; set; }
    }

    private sealed class Unrelated
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid CustomerId { get; set; }
    }

    private sealed class TrackingContext(DbContextOptions<TrackingContext> options) : DbContext(options)
    {
        public static TrackingContext For(string connectionString) =>
            new(new DbContextOptionsBuilder<TrackingContext>().UseNpgsql(connectionString).Options);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Low>();
            modelBuilder.Entity<High>();
            modelBuilder.Entity<Unrelated>();
        }
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(Ct);
        return database;
    }
}
