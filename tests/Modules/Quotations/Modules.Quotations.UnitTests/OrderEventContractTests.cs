using System.Text.Json;
using BuildingBlocks.Domain.Catalog;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El contrato de los eventos de pedido que consumen Pipeline (<c>QuotationEventPayloads</c>), Shopify
/// (<c>QuotationOrderEventPayloads</c>), Compras (<c>WholesaleOrderEventPayloads</c>) y el inventario básico del WMS. Cada prueba
/// serializa el payload con el publicador real y fija <b>los campos viejos con su nombre exacto</b> (ninguno se renombra ni se
/// quita: los lectores los toman por nombre) además de los nuevos, que sólo se agregan al final.
/// </summary>
public sealed class OrderEventContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());

    private readonly QuotationsDbContext _context = new QuotationsDbContextFactory().CreateDbContext([]);

    private QuotationLifecycleEventPublisher Publisher() => new(_context);

    private JsonElement Single(string eventName)
    {
        var message = Assert.Single(_context.Outbox.Local, candidate => candidate.EventName == eventName);
        return JsonDocument.Parse(message.PayloadJson).RootElement.Clone();
    }

    private static Order ManualOrder(params OrderLineInput[] lines) =>
        Order.CreateManual(
            OrderId.New(),
            TenantId,
            "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), Advisor, "COP", null, IsRetail: false, null, false, []),
            QuotationParties.Empty,
            OrderBuilders.BillingAccount,
            ShippingDetails.None,
            lines,
            notes: null,
            Advisor,
            [],
            Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    private static OrderLineInput PhysicalLine(decimal quantity = 2m) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), quantity, 119_000m, 0m, 19);

    private static Order ApprovedWholesaleOrder(out Guid sourceLineId)
    {
        sourceLineId = Guid.CreateVersion7();
        var order = WholesaleOrderFixtures.Wholesale(
            WholesaleOrderFixtures.Line(Guid.CreateVersion7(), 4m, 10_000m, sourceLineId));
        order.ApproveWholesale(WholesaleOrderFixtures.Advisor, WholesaleOrderFixtures.Now);
        return order;
    }

    [Fact]
    public void CreatedKeepsItsFieldsAndAddsVersionAndLines()
    {
        var order = ManualOrder(PhysicalLine(2m), PhysicalLine(5m));

        Publisher().PublishOrderCreated(order, Now);

        var payload = Single("quotations.order.created.v1");
        // Los que ya leía Pipeline, por nombre.
        Assert.Equal(order.TenantId, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(order.Id.Value, payload.GetProperty("orderId").GetGuid());
        Assert.Equal(order.OrderNumber, payload.GetProperty("orderNumber").GetString());
        Assert.Equal(order.ClientId, payload.GetProperty("clientId").GetGuid());
        Assert.Equal(order.AdvisorId.Value, payload.GetProperty("advisorMemberId").GetGuid());
        Assert.Equal(order.Total, payload.GetProperty("total").GetDecimal());
        Assert.Equal("COP", payload.GetProperty("currency").GetString());
        Assert.Equal(order.Source.ToString(), payload.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("quotationId").ValueKind);
        Assert.Equal(Now, payload.GetProperty("occurredAt").GetDateTimeOffset());
        // Los nuevos.
        Assert.Equal(order.Version, payload.GetProperty("version").GetInt64());
        Assert.False(payload.GetProperty("awaitingWholesaleApproval").GetBoolean());
        var lines = payload.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(
            order.Items.OrderBy(item => item.Position).Select(item => (item.Id.Value, item.VariantId, item.Quantity)).ToArray(),
            lines.Select(line => (line.GetProperty("lineId").GetGuid(), line.GetProperty("variantId").GetGuid(), line.GetProperty("quantity").GetDecimal())).ToArray());
        Assert.All(lines, line => Assert.Equal("Physical", line.GetProperty("productKind").GetString()));
    }

    [Fact]
    public void CreatedOfAnUnapprovedWholesaleOrderIsMarkedAsAwaitingApproval()
    {
        var order = WholesaleOrderFixtures.Wholesale(WholesaleOrderFixtures.Line(Guid.CreateVersion7(), 4m, 10_000m));

        Publisher().PublishOrderCreated(order, Now);

        Assert.True(Single("quotations.order.created.v1").GetProperty("awaitingWholesaleApproval").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledAddsVersionAndFromPlatform(bool fromPlatform)
    {
        var order = ManualOrder(PhysicalLine());
        order.Cancel(Advisor, "Ya no lo quiere", Now);

        Publisher().PublishOrderCancelled(order, fromPlatform, Now);

        var payload = Single("quotations.order.cancelled.v1");
        // Los que ya leían Pipeline y el resto de los consumidores.
        Assert.Equal(order.TenantId, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(order.Id.Value, payload.GetProperty("orderId").GetGuid());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("quotationId").ValueKind);
        Assert.Equal(order.Source.ToString(), payload.GetProperty("source").GetString());
        Assert.Equal(Now, payload.GetProperty("occurredAt").GetDateTimeOffset());
        // Los nuevos.
        Assert.Equal(order.Version, payload.GetProperty("version").GetInt64());
        Assert.Equal(fromPlatform, payload.GetProperty("fromPlatform").GetBoolean());
    }

    [Fact]
    public void WholesaleApprovedAddsVersionOccurredAtAndLineIds()
    {
        var order = ApprovedWholesaleOrder(out var sourceLineId);

        Publisher().PublishWholesaleApproved(order, Now);

        var payload = Single("quotations.order.wholesale-approved.v1");
        // Los que ya leía Compras, por nombre.
        Assert.Equal(order.TenantId, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(order.Id.Value, payload.GetProperty("orderId").GetGuid());
        Assert.Equal(order.OrderNumber, payload.GetProperty("orderNumber").GetString());
        Assert.Equal(WholesaleOrderFixtures.BuyerTenantId, payload.GetProperty("buyerTenantId").GetGuid());
        Assert.Equal(WholesaleOrderFixtures.PurchaseOrderId, payload.GetProperty("purchaseOrderId").GetGuid());
        Assert.Equal("COP", payload.GetProperty("currency").GetString());
        Assert.Equal(order.WholesaleApprovedAt, payload.GetProperty("approvedAt").GetDateTimeOffset());
        var line = Assert.Single(payload.GetProperty("lines").EnumerateArray());
        Assert.Equal(sourceLineId, line.GetProperty("sourcePurchaseOrderLineId").GetGuid());
        Assert.Equal(order.Items.Single().VariantId, line.GetProperty("variantId").GetGuid());
        Assert.False(string.IsNullOrEmpty(line.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrEmpty(line.GetProperty("name").GetString()));
        Assert.Equal(4m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(order.Items.Single().UnitPrice, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(order.Items.Single().TaxPercentage, line.GetProperty("taxPercentage").GetDecimal());
        // Los nuevos.
        Assert.Equal(order.Version, payload.GetProperty("version").GetInt64());
        Assert.Equal(Now, payload.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(order.Items.Single().Id.Value, line.GetProperty("lineId").GetGuid());
        Assert.Equal("Physical", line.GetProperty("productKind").GetString());
    }

    [Fact]
    public void AServiceLineTravelsWithServiceKind()
    {
        var order = ManualOrder(
            PhysicalLine(),
            new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 50_000m, 0m, 19, Kind: ProductKind.Service));

        Publisher().PublishOrderCreated(order, Now);

        var kinds = Single("quotations.order.created.v1").GetProperty("lines").EnumerateArray()
            .Select(line => line.GetProperty("productKind").GetString()!)
            .ToArray();
        Assert.Equal(["Physical", "Service"], kinds);
    }

    private static void AssertCommon(JsonElement payload, Order order)
    {
        Assert.Equal(order.TenantId, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(order.Id.Value, payload.GetProperty("orderId").GetGuid());
        Assert.Equal(order.Version, payload.GetProperty("version").GetInt64());
        Assert.Equal(Now, payload.GetProperty("occurredAt").GetDateTimeOffset());
    }

    private static void AssertLines(JsonElement payload, Order order) =>
        Assert.Equal(
            order.Items.OrderBy(item => item.Position).Select(item => (item.Id.Value, item.VariantId, item.Quantity, "Physical")).ToArray(),
            payload.GetProperty("lines").EnumerateArray()
                .Select(line => (line.GetProperty("lineId").GetGuid(), line.GetProperty("variantId").GetGuid(), line.GetProperty("quantity").GetDecimal(), line.GetProperty("productKind").GetString()!))
                .ToArray());

    [Fact]
    public void EditedCarriesTheCommonFieldsAndAllTheLines()
    {
        var order = ManualOrder(PhysicalLine(2m), PhysicalLine(5m));

        Publisher().PublishOrderEdited(order, Now);

        var payload = Single("quotations.order.edited.v1");
        AssertCommon(payload, order);
        AssertLines(payload, order);
    }

    [Fact]
    public void PreparedCarriesTheOriginAndTheLines()
    {
        var order = ManualOrder(PhysicalLine(2m));
        order.Prepare(Advisor, Now);

        Publisher().PublishOrderPrepared(order, OrderEventOrigins.WmsPacking, Now);

        var payload = Single("quotations.order.prepared.v1");
        AssertCommon(payload, order);
        Assert.Equal("WmsPacking", payload.GetProperty("origin").GetString());
        AssertLines(payload, order);
    }

    [Fact]
    public void UnpreparedCarriesOnlyTheCommonFields()
    {
        var order = ManualOrder(PhysicalLine());
        order.Prepare(Advisor, Now);
        order.UndoPreparation(Now);

        Publisher().PublishOrderUnprepared(order, Now);

        var payload = Single("quotations.order.unprepared.v1");
        AssertCommon(payload, order);
        Assert.False(payload.TryGetProperty("lines", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchivedAndUnarchivedCarryIsPrepared(bool prepared)
    {
        var order = ManualOrder(PhysicalLine(3m));
        if (prepared)
        {
            order.Prepare(Advisor, Now);
        }

        order.Archive(Advisor, Now);
        Publisher().PublishOrderArchived(order, Now);
        var archived = Single("quotations.order.archived.v1");
        order.Unarchive(Now);
        Publisher().PublishOrderUnarchived(order, Now);
        var unarchived = Single("quotations.order.unarchived.v1");

        Assert.Equal(prepared, archived.GetProperty("isPrepared").GetBoolean());
        Assert.Equal(prepared, unarchived.GetProperty("isPrepared").GetBoolean());
        Assert.Equal(order.TenantId, archived.GetProperty("tenantId").GetGuid());
        Assert.Equal(order.Id.Value, unarchived.GetProperty("orderId").GetGuid());
        Assert.True(unarchived.GetProperty("version").GetInt64() > archived.GetProperty("version").GetInt64());
        Assert.Equal(1, archived.GetProperty("lines").GetArrayLength());
        Assert.False(unarchived.TryGetProperty("lines", out _));
    }
}
