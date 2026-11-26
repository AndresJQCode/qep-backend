using System.Collections.Concurrent;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// Quién timbra (spec 2026-11-13, D-7; plan P10 y P12): el interceptor de EF reconoce un <c>INSERT INTO</c> de una tabla de
/// cola, por <c>SaveChanges</c> o por SQL crudo, y timbra su cola después del commit. PostgreSQL real, un contenedor para la
/// clase: el texto que se reconoce lleva el esquema real, así que las tablas se crean una vez y cada prueba sólo inserta ids
/// nuevos.
/// </summary>
public sealed class QueueDoorbellInterceptorTests(DoorbellDatabase database) : IClassFixture<DoorbellDatabase>
{
    [Theory]
    [InlineData("platform.outbox_messages", DoorbellQueues.Outbox)]
    [InlineData("messaging.automation_inbox", DoorbellQueues.Automation)]
    [InlineData("messaging.webhook_deliveries", DoorbellQueues.Webhook)]
    [InlineData("shopify.webhook_deliveries", DoorbellQueues.ShopifyWebhook)]
    [InlineData("messaging.message_media", DoorbellQueues.Media)]
    [InlineData("messaging.scheduled_messages", DoorbellQueues.Scheduled)]
    [InlineData("quotations.export_jobs", DoorbellQueues.Export)]
    [InlineData("support.ticket_surveys", DoorbellQueues.SupportSurvey)]
    [InlineData("shopify.product_imports", DoorbellQueues.ShopifyProducts)]
    [InlineData("siigo.invoice_batch_items", DoorbellQueues.SiigoInvoice)]
    public async Task InsertIntoEachDoorbellTableRingsItsQueue(string table, string queue)
    {
        var doorbell = new RecordingDoorbell();
        await using var context = database.NewContext(doorbell);

        var sql = "INSERT INTO " + table + " (id) VALUES ({0})";
        await context.Database.ExecuteSqlRawAsync(sql, [Guid.CreateVersion7()], Ct);

        Assert.Equal([queue], doorbell.Rings);
    }

    // El outbox lo escriben ~20 DbContext con SaveChanges: el batch de EF pasa por ReaderExecuted, dentro de su transacción.
    [Fact]
    public async Task ASaveChangesInsertRingsAfterItsCommit()
    {
        var doorbell = new RecordingDoorbell();
        await using var context = database.NewContext(doorbell);
        context.Outbox.Add(new OutboxRow { Id = Guid.CreateVersion7() });

        await context.SaveChangesAsync(Ct);

        Assert.Equal([DoorbellQueues.Outbox], doorbell.Rings);
    }

    [Fact]
    public async Task AnInsertInsideATransactionRingsOnlyAfterCommit()
    {
        var doorbell = new RecordingDoorbell();
        await using var context = database.NewContext(doorbell);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        await context.Database.ExecuteSqlAsync($"INSERT INTO messaging.automation_inbox (id) VALUES ({Guid.CreateVersion7()})", Ct);
        Assert.Empty(doorbell.Rings);

        await transaction.CommitAsync(Ct);
        Assert.Equal([DoorbellQueues.Automation], doorbell.Rings);
    }

    [Fact]
    public async Task ARolledBackInsertDoesNotRing()
    {
        var doorbell = new RecordingDoorbell();
        await using var context = database.NewContext(doorbell);
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            await context.Database.ExecuteSqlAsync($"INSERT INTO messaging.webhook_deliveries (id) VALUES ({Guid.CreateVersion7()})", Ct);
            await transaction.RollbackAsync(Ct);
        }

        // Una transacción nueva y confirmada sin inserciones no hace sonar lo que se deshizo.
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            await context.Database.ExecuteSqlRawAsync("SELECT 1", Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Empty(doorbell.Rings);
    }

    [Fact]
    public async Task AnInsertThatInsertedNothingDoesNotRing()
    {
        var doorbell = new RecordingDoorbell();
        await using var context = database.NewContext(doorbell);
        var id = Guid.CreateVersion7();
        await context.Database.ExecuteSqlAsync($"INSERT INTO messaging.message_media (id) VALUES ({id})", Ct);
        doorbell.Clear();

        await context.Database.ExecuteSqlAsync($"INSERT INTO messaging.message_media (id) VALUES ({id}) ON CONFLICT DO NOTHING", Ct);

        Assert.Empty(doorbell.Rings);
    }

    [Fact]
    public async Task ATableThatIsNotAQueueDoesNotRing()
    {
        var doorbell = new RecordingDoorbell();
        await using var context = database.NewContext(doorbell);

        await context.Database.ExecuteSqlAsync($"INSERT INTO platform.outbox_messages_archive (id) VALUES ({Guid.CreateVersion7()})", Ct);

        Assert.Empty(doorbell.Rings);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class RecordingDoorbell : IQueueDoorbell
    {
        private readonly ConcurrentQueue<string> _rings = new();

        public IReadOnlyList<string> Rings => [.. _rings];

        public void Clear() => _rings.Clear();

        public void Ring(string queue) => _rings.Enqueue(queue);

        public IDoorbellListener Listen(string queue) => throw new NotSupportedException();
    }
}

/// <summary>Un PostgreSQL con las ocho tablas de cola reducidas a su <c>id</c>, para toda la clase.</summary>
public sealed class DoorbellDatabase : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await using var context = NewContext(new QueueDoorbellInterceptorTests.RecordingDoorbell());
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE SCHEMA platform; CREATE SCHEMA messaging; CREATE SCHEMA shopify; CREATE SCHEMA quotations; CREATE SCHEMA support; CREATE SCHEMA siigo;
            CREATE TABLE platform.outbox_messages (id uuid PRIMARY KEY);
            CREATE TABLE platform.outbox_messages_archive (id uuid PRIMARY KEY);
            CREATE TABLE messaging.automation_inbox (id uuid PRIMARY KEY);
            CREATE TABLE messaging.webhook_deliveries (id uuid PRIMARY KEY);
            CREATE TABLE shopify.webhook_deliveries (id uuid PRIMARY KEY);
            CREATE TABLE messaging.message_media (id uuid PRIMARY KEY);
            CREATE TABLE messaging.scheduled_messages (id uuid PRIMARY KEY);
            CREATE TABLE quotations.export_jobs (id uuid PRIMARY KEY);
            CREATE TABLE support.ticket_surveys (id uuid PRIMARY KEY);
            CREATE TABLE shopify.product_imports (id uuid PRIMARY KEY);
            CREATE TABLE siigo.invoice_batch_items (id uuid PRIMARY KEY);
            """,
            TestContext.Current.CancellationToken);
    }

    internal DoorbellContext NewContext(IQueueDoorbell doorbell) =>
        new(new DbContextOptionsBuilder<DoorbellContext>()
            .UseNpgsql(_container.GetConnectionString())
            .AddInterceptors(new QueueDoorbellInterceptor(doorbell))
            .Options);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

internal sealed class OutboxRow
{
    public Guid Id { get; set; }
}

internal sealed class DoorbellContext(DbContextOptions<DoorbellContext> options) : DbContext(options)
{
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<OutboxRow>(entity =>
        {
            entity.ToTable("outbox_messages", "platform");
            entity.Property(row => row.Id).HasColumnName("id");
        });
}
