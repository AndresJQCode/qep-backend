using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure.Persistence;
using static Modules.Wms.IntegrationTests.AdjustmentTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>Las sondas de «con existencias» contra el libro real (RF-010, P-3 de WMS-B, §17): saldo físico, no virtual y distinto de cero.</summary>
public sealed class StockProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static HttpRequestMessage Write(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Client", "web");
        return request;
    }

    [Fact]
    public async Task AVariantWithPhysicalStockHasStockAndOneWithOnlyVirtualDoesNot()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var withoutBalance = Guid.CreateVersion7();
        await StockAsync(factory, s, s.BinA, 5m);

        await using var scope = factory.Services.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<IVariantStockProbe>();

        // El bin tiene 5; el proveedor quedó en -5, pero es virtual y no cuenta. Una variante sin movimientos no tiene saldo.
        Assert.True(await probe.HasStockAsync(s.TenantId, s.VariantId, Ct));
        Assert.False(await probe.HasStockAsync(s.TenantId, withoutBalance, Ct));
        Assert.False(await probe.HasStockAsync(Guid.CreateVersion7(), s.VariantId, Ct));

        // Devolver todo al proveedor deja el bin en cero: la fila sigue, el saldo no.
        await PostAsync(factory, Batch(s, Entry(s, s.BinA, StockStatus.Available, s.Supplier, StockStatus.Available, 5m)));
        Assert.False(await probe.HasStockAsync(s.TenantId, s.VariantId, Ct));
    }

    [Fact]
    public async Task OnlyVirtualBalancesDoNotCountAsStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        // Un movimiento entre dos virtuales (proveedor a ajuste) deja saldo distinto de cero sólo en ubicaciones virtuales.
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, StockStatus.Available, s.Adjustment, StockStatus.Available, 7m)));

        await using var scope = factory.Services.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<IVariantStockProbe>();
        Assert.False(await probe.HasStockAsync(s.TenantId, s.VariantId, Ct));
        Assert.Empty(await probe.HasStockForManyAsync(s.TenantId, [s.VariantId], Ct));
    }

    [Fact]
    public async Task TheBatchProbeAnswersTwoHundredVariantsInOneQuery()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var second = await CreateVariantAsync(factory, "V2", null, []);
        await StockAsync(factory, s, s.BinA, 5m);
        await StockAsync(factory, s, s.BinA, 2m, variant: second);
        var ids = Enumerable.Range(0, 198).Select(_ => Guid.CreateVersion7()).Append(s.VariantId).Append(second).ToArray();

        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<WmsDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .AddInterceptors(counter)
            .Options;
        await using var dbContext = new WmsDbContext(options);

        var found = await new VariantStockProbe(dbContext).HasStockForManyAsync(s.TenantId, ids, Ct);

        Assert.Equal(1, counter.Texts.Count(text => text.Contains("wms.stock_balances", StringComparison.Ordinal)));
        Assert.Equal(new HashSet<Guid> { s.VariantId, second }, found.ToHashSet());
    }

    [Fact]
    public async Task ALocationWithStockCannotBeDeleted()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 3m);

        var response = await s.Manager.SendAsync(
            Write(HttpMethod.Delete, $"{WmsUrl()}/warehouses/{s.WarehouseId}/locations/{s.BinA}"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.has_stock", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnOwnerWithStockCannotBeDeactivatedButOneWithoutStockCan()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var withStock = await CreateOwnerAsync(s.Manager, "Con saldo");
        var withoutStock = await CreateOwnerAsync(s.Manager, "Sin saldo");
        await PostAsync(factory, Batch(s, Entry(s with { OwnerId = withStock }, s.Supplier, StockStatus.Available, s.BinA, StockStatus.Available, 4m)));

        var blocked = await s.Manager.SendAsync(Write(HttpMethod.Put, $"{WmsUrl()}/owners/{withStock}", UpdateBody("Con saldo", false)), Ct);
        var allowed = await s.Manager.SendAsync(Write(HttpMethod.Put, $"{WmsUrl()}/owners/{withoutStock}", UpdateBody("Sin saldo", false)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal("wms.owner.has_stock", await CodeOfAsync(blocked));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task TheRealCheckerSeesAWmsMovementAgainstTheMigratedSchema()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var untouched = Guid.CreateVersion7();
        await PostedAsync(s.Manager, s, s.BinA, 2m);

        await using var scope = factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<Modules.Catalog.Application.IVariantUsageChecker>();
        var used = await checker.FindUsedAsync(s.TenantId, [s.VariantId, untouched], Ct);
        var fromAnotherTenant = await checker.FindUsedAsync(Guid.CreateVersion7(), [s.VariantId], Ct);

        Assert.Equal(new HashSet<Guid> { s.VariantId }, used.ToHashSet());
        Assert.Empty(fromAnotherTenant);
    }

    // Un ajuste Proposed sobre una variante sin movimientos: sin esto Catalog borraba la variante y el posteo escribía después un
    // movimiento inmutable sobre una variante inexistente. Un ajuste Posted ya lo cubre su movimiento; uno Rejected no escribe nada.
    [Fact]
    public async Task TheRealCheckerSeesAnOpenAdjustmentLineAndALotAgainstTheMigratedSchema()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var proposedVariant = await CreateVariantAsync(factory, "V-PROPOSED", null, [12]);
        var rejectedVariant = await CreateVariantAsync(factory, "V-REJECTED", null, [12]);
        var lotVariant = await CreateVariantAsync(factory, "V-LOT", null, [12]);
        var untouched = await CreateVariantAsync(factory, "V-FREE", null, [12]);
        await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, proposedVariant, 3m)]));
        var rejected = await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, rejectedVariant, 3m)]));
        var rejection = await ActionRawAsync(s.Manager, rejected.Id, "reject", new { version = rejected.Version, note = "no" });
        Assert.Equal(HttpStatusCode.OK, rejection.StatusCode);
        await CreateLotAsync(factory, s, lotVariant, "LOT-1");

        await using var scope = factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<Modules.Catalog.Application.IVariantUsageChecker>();
        var used = await checker.FindUsedAsync(
            s.TenantId, [proposedVariant, rejectedVariant, lotVariant, untouched], Ct);
        var fromAnotherTenant = await checker.FindUsedAsync(Guid.CreateVersion7(), [proposedVariant, lotVariant], Ct);

        Assert.Equal(new HashSet<Guid> { proposedVariant, lotVariant }, used.ToHashSet());
        Assert.Empty(fromAnotherTenant);
    }

    // DA-37: una tarea abierta todavía va a escribir en el ledger. La cancelada no escribe nada.
    [Fact]
    public async Task TheRealCheckerSeesAnOpenTaskLineAgainstTheMigratedSchema()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await Tasks.TaskTestSupport.CreateDestinationAsync(s);
        var openVariant = await CreateVariantAsync(factory, "V-OPEN-TASK", null, [12]);
        var cancelledVariant = await CreateVariantAsync(factory, "V-CANCELLED-TASK", null, [12]);
        var open = Tasks.TaskTestSupport.NewMove(s with { VariantId = openVariant }, s.BinA, destination, publish: false);
        var cancelled = Tasks.TaskTestSupport.NewMove(s with { VariantId = cancelledVariant }, s.BinA, destination);
        cancelled.Cancel("ya no", Guid.CreateVersion7(), DateTimeOffset.UtcNow);
        await Tasks.TaskTestSupport.SaveAsync(factory, open, cancelled);

        await using var scope = factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<Modules.Catalog.Application.IVariantUsageChecker>();
        var used = await checker.FindUsedAsync(s.TenantId, [openVariant, cancelledVariant], Ct);
        var fromAnotherTenant = await checker.FindUsedAsync(Guid.CreateVersion7(), [openVariant], Ct);

        Assert.Equal(new HashSet<Guid> { openVariant }, used.ToHashSet());
        Assert.Empty(fromAnotherTenant);
    }

    // DA-51: una novedad abierta (puede escribir al ledger al resolverse) protege la variante; la resuelta o rechazada, no.
    [Fact]
    public async Task TheRealCheckerSeesAnOpenIncidentAgainstTheMigratedSchema()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var openVariant = await CreateVariantAsync(factory, "V-OPEN-INCIDENT", null, [12]);
        var closedVariant = await CreateVariantAsync(factory, "V-CLOSED-INCIDENT", null, [12]);
        await Incidents.IncidentTestSupport.NumberAndSaveAsync(
            factory, s, number => Incidents.IncidentTestSupport.NewIncident(s with { VariantId = openVariant }, number));
        var closed = await Incidents.IncidentTestSupport.NumberAndSaveAsync(
            factory, s, number => Incidents.IncidentTestSupport.NewIncident(s with { VariantId = closedVariant }, number));
        await ExecuteAsync(database, "UPDATE wms.incidents SET status = 'Rejected' WHERE id = @id", ("id", closed.Id));

        await using var scope = factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<Modules.Catalog.Application.IVariantUsageChecker>();
        var used = await checker.FindUsedAsync(s.TenantId, [openVariant, closedVariant], Ct);
        var fromAnotherTenant = await checker.FindUsedAsync(Guid.CreateVersion7(), [openVariant], Ct);

        Assert.Equal(new HashSet<Guid> { openVariant }, used.ToHashSet());
        Assert.Empty(fromAnotherTenant);
    }

    [Fact]
    public async Task TheRealCheckerSeesAnOpenCountLineAgainstTheMigratedSchema()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var openVariant = await CreateVariantAsync(factory, "V-OPEN-COUNT", null, [12]);
        var doneVariant = await CreateVariantAsync(factory, "V-DONE-COUNT", null, [12]);
        var open = Tasks.TaskTestSupport.NewCount(s with { VariantId = openVariant }, s.BinA);
        var done = Tasks.TaskTestSupport.NewCount(s with { VariantId = doneVariant }, s.BinA);
        var member = Guid.CreateVersion7();
        done.Claim(member, "device-1", DateTimeOffset.UtcNow);
        done.RecordCount(member, done.LeaseToken!.Value, "device-1", done.Lines[0].Id, 1, null, DateTimeOffset.UtcNow);
        await Tasks.TaskTestSupport.SaveAsync(factory, open, done);

        await using var scope = factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<Modules.Catalog.Application.IVariantUsageChecker>();
        var used = await checker.FindUsedAsync(s.TenantId, [openVariant, doneVariant], Ct);

        // Un Count abierto no escribe al ledger, pero su línea abierta cuenta como cualquier otra: el supervisor la ve en el tablero.
        Assert.Equal(new HashSet<Guid> { openVariant }, used.ToHashSet());
    }

    private static object UpdateBody(string name, bool isActive) =>
        new { name, identification = "900123", contactEmail = "acme@example.com", isActive, version = 1 };

    private static async Task<Guid> CreateOwnerAsync(HttpClient client, string name)
    {
        var response = await client.SendAsync(
            Write(HttpMethod.Post, $"{WmsUrl()}/owners", new { name, identification = "900123", contactEmail = "acme@example.com" }), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("id").GetGuid();
    }
}
