using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La foto de retenciones del pedido ida y vuelta por el DbContext (spec impuestos por tipo §7.5, §8): la tabla
/// <c>quotations.order_withholdings</c>, con una sola fuente ('Document').
/// </summary>
public sealed class OrderWithholdingsPersistenceTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-00000000f021");
    private static readonly MemberId Advisor = new(Guid.Parse("01900000-0000-7000-8000-00000000f022"));
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount ColombianAccount = new()
    {
        CompanyId = Guid.Parse("01900000-0000-7000-8000-00000000f023"),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
        Country = "CO",
    };

    private static readonly WithholdingRate Retefuente =
        new(Guid.CreateVersion7(), "Retefuente 2,5 %", "Retefuente", "CO", 2.5m, null, null, null);

    // 4 UVT x 49.799 = 199.196: con 100.000 de base no aplica.
    private static readonly WithholdingRate ReteIca =
        new(Guid.CreateVersion7(), "ReteICA Bogotá 0,966 %", "ReteICA", "CO", 0.966m, 4m, "UVT", 49_799m);

    private static readonly WithholdingRate RetefuenteServicios =
        new(Guid.CreateVersion7(), "Retefuente servicios 4 %", "Retefuente", "CO", 4m, null, null, null);

    [Fact]
    public async Task PhotoRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateAsync(connectionString);

        var order = NewOrder([ReteIca, Retefuente]);
        await SaveNewAsync(connectionString, order);

        await using var read = NewContext(connectionString);
        var reloaded = await FindAsync(read, order.Id);

        Assert.Equal("CO", reloaded.BillingAccount!.Country);
        var rows = reloaded.Withholdings.OrderBy(row => row.Position).ToArray();
        Assert.Equal([ReteIca, Retefuente], rows.Select(row => row.Rate));
        Assert.All(rows, row => Assert.Equal(WithholdingSource.Document, row.Source));
        Assert.Equal([(0, false, 0m, 0m), (1, true, 100_000m, 2_500m)],
            rows.Select(row => ((int)row.Position, row.Applied, row.BaseAmount, row.Amount)));
        Assert.Equal((2_500m, reloaded.Total - 2_500m), (reloaded.RetentionAmount, reloaded.NetTotal));
    }

    // Lección de customer_tax_rates (carry de la Tarea 9): agregar una fila a un pedido ya rastreado tiene que salir
    // como INSERT y no como UPDATE de 0 filas (412). Acá se agrega, se cambia y se quita sobre un pedido existente.
    [Fact]
    public async Task WithholdingsCanBeAddedChangedAndRemovedOnAnExistingOrder()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateAsync(connectionString);

        var order = NewOrder([]);
        await SaveNewAsync(connectionString, order);

        await using (var add = NewContext(connectionString))
        {
            var tracked = await FindAsync(add, order.Id);
            Assert.True(tracked.SetWithholdings([Retefuente], Advisor, Now));
            await new QuotationsUnitOfWork(add).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal([(Retefuente.TaxRateId, 2_500m)], await RowsAsync(connectionString, order.Id));

        await using (var change = NewContext(connectionString))
        {
            var tracked = await FindAsync(change, order.Id);
            Assert.True(tracked.SetWithholdings([RetefuenteServicios, ReteIca], Advisor, Now));
            await new QuotationsUnitOfWork(change).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [(RetefuenteServicios.TaxRateId, 4_000m), (ReteIca.TaxRateId, 0m)],
            await RowsAsync(connectionString, order.Id));

        await using (var clear = NewContext(connectionString))
        {
            var tracked = await FindAsync(clear, order.Id);
            Assert.True(tracked.SetWithholdings([], Advisor, Now));
            await new QuotationsUnitOfWork(clear).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(await RowsAsync(connectionString, order.Id));
        await using var read = NewContext(connectionString);
        var reloaded = await FindAsync(read, order.Id);
        Assert.Equal(0m, reloaded.RetentionAmount);
        // Alta 1 y las tres ediciones de los selectores: cada una es una versión.
        Assert.Equal(4, reloaded.Version);
    }

    // D22–D23: fijar otra empresa en un pedido pendiente guarda su país, y la tasa de otro país deja de aplicar sin
    // borrarse de la foto.
    [Fact]
    public async Task ChangingTheBillingAccountPersistsItsCountry()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateAsync(connectionString);

        var order = NewOrder([Retefuente]);
        await SaveNewAsync(connectionString, order);

        await using (var edit = NewContext(connectionString))
        {
            var tracked = await FindAsync(edit, order.Id);
            Assert.True(tracked.SetBillingAccount(
                ColombianAccount with { CompanyId = Guid.CreateVersion7(), Country = "ES" }, Advisor, Now));
            await new QuotationsUnitOfWork(edit).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = NewContext(connectionString);
        var reloaded = await FindAsync(read, order.Id);
        Assert.Equal("ES", reloaded.BillingAccount!.Country);
        Assert.Equal(0m, reloaded.RetentionAmount);
        var row = Assert.Single(reloaded.Withholdings);
        Assert.Equal((Retefuente, false, 0m), (row.Rate, row.Applied, row.Amount));
    }

    // 119.000 con el IVA del 19 % adentro: base sin IVA 100.000.
    private static Order NewOrder(IReadOnlyList<WithholdingRate> withholdings) =>
        Order.CreateManual(
            OrderId.New(),
            TenantId,
            $"PED-{Guid.CreateVersion7():N}"[..20],
            new OrderHeader(
                Guid.CreateVersion7(), Advisor, "COP", null, IsRetail: false, null, CustomerVatSurplus: false, withholdings),
            QuotationParties.Empty,
            ColombianAccount,
            ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19)],
            OrderPaymentStatus.PaymentPending,
            null,
            Advisor,
            [],
            Now);

    private static async Task SaveNewAsync(string connectionString, Order order)
    {
        await using var create = NewContext(connectionString);
        create.Orders.Add(order);
        await new QuotationsUnitOfWork(create).SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // Como OrderRepository.FindByIdAsync: con comprobantes, líneas y partes, que el recálculo necesita. La foto de
    // retenciones es owned y llega sola.
    private static Task<Order> FindAsync(QuotationsDbContext context, OrderId id) =>
        context.Orders
            .Include(order => order.PaymentProofs)
            .Include(order => order.Items)
            .Include(order => order.Parties)
            .SingleAsync(order => order.Id == id, TestContext.Current.CancellationToken);

    private static async Task<List<(Guid TaxRateId, decimal Amount)>> RowsAsync(string connectionString, OrderId orderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT tax_rate_id, amount FROM quotations.order_withholdings " +
            "WHERE document_id = @id AND source = 'Document' ORDER BY position",
            connection);
        command.Parameters.AddWithValue("id", orderId.Value);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(Guid, decimal)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetGuid(0), reader.GetDecimal(1)));
        }

        return rows;
    }

    private static async Task MigrateAsync(string connectionString)
    {
        await using var setup = NewContext(connectionString);
        await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);
}
