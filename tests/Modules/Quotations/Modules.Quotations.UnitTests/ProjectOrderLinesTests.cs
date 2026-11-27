using BuildingBlocks.Domain.Catalog;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>El candado de las líneas de un pedido de proyecto al editarlo (spec 2026-11-26 §6.6 y §12-C): sin líneas, o
/// con exactamente la que hay, no cambia nada; cualquier otra cosa es <c>order.project.items_locked</c>.</summary>
public sealed class ProjectOrderLinesTests
{
    private static readonly Guid ServiceVariantId = Guid.CreateVersion7();

    [Fact]
    public void EmptyOrTheSameLineKeepsIt()
    {
        var order = ProjectOrders.Converted(ServiceVariantId, 1_000_000m);

        Assert.True(ProjectOrderLines.KeepsTheLine(order, [], gifts: null));
        Assert.True(ProjectOrderLines.KeepsTheLine(order, [new OrderItemAddition(ServiceVariantId, 1m)], gifts: null));
        Assert.True(ProjectOrderLines.KeepsTheLine(
            order, [new OrderItemAddition(ServiceVariantId, 1m, 1_000_000m)], gifts: []));
    }

    [Fact]
    public void AnotherQuantityVariantOrPriceIsLocked()
    {
        var order = ProjectOrders.Converted(ServiceVariantId, 1_000_000m);
        var line = new OrderItemAddition(ServiceVariantId, 1m);
        OrderItemAddition[][] changes =
        [
            [new(ServiceVariantId, 2m)],
            [new(Guid.CreateVersion7(), 1m)],
            [new(ServiceVariantId, 1m, 999_999m)],
            [line, new(Guid.CreateVersion7(), 1m)],
            [line, line],
        ];

        foreach (var items in changes)
        {
            AssertLocked(() => ProjectOrderLines.KeepsTheLine(order, items, gifts: null));
        }
    }

    [Fact]
    public void GiftsWithElementsAreLockedAndAnEmptyListIsNot()
    {
        var order = ProjectOrders.Converted(ServiceVariantId, 1_000_000m);

        Assert.True(ProjectOrderLines.KeepsTheLine(order, [], gifts: []));
        AssertLocked(() => ProjectOrderLines.KeepsTheLine(order, [], [new OrderItemAddition(ServiceVariantId, 1m)]));
    }

    [Fact]
    public void AStandardOrderIsNotAffected()
    {
        var order = Order.CreateManual(
            OrderId.New(), Guid.CreateVersion7(), "PED-2026-0001",
            new OrderHeader(
                Guid.CreateVersion7(), new MemberId(Guid.CreateVersion7()), "COP", null, IsRetail: false, null, false, []),
            QuotationParties.Empty, ProjectOrders.BillingAccount, ShippingDetails.None,
            [new OrderLineInput(ServiceVariantId, ServiceVariantId, 1m, 10_000m, 0m, 19)],
            null, new MemberId(Guid.CreateVersion7()), [], ProjectOrders.Now,
            orderDate: DateOnly.FromDateTime(ProjectOrders.Now.UtcDateTime));

        Assert.False(ProjectOrderLines.KeepsTheLine(order, [], gifts: null));
        Assert.False(ProjectOrderLines.KeepsTheLine(
            order, [new OrderItemAddition(Guid.CreateVersion7(), 5m)], [new OrderItemAddition(ServiceVariantId, 1m)]));
    }

    private static void AssertLocked(Func<bool> check)
    {
        var error = Assert.Throws<QuotationsDomainException>(() => { check(); });

        Assert.Equal(ProjectErrors.ItemsLocked, error.Code);
    }
}

/// <summary>Un pedido de proyecto como nace de verdad: de una cotización de proyecto convertida.</summary>
internal static class ProjectOrders
{
    public static readonly DateTimeOffset Now = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

    public static readonly QuotationBillingAccount BillingAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
        Country = "CO",
    };

    public static Order Converted(Guid serviceVariantId, decimal price)
    {
        var member = new MemberId(Guid.CreateVersion7());
        var quotation = Quotation.Create(
            QuotationId.New(), Guid.CreateVersion7(), "COT-2026-0001", Guid.CreateVersion7(), member,
            new DateOnly(2026, 12, 31), paymentMethod: null, notes: null, QuotationParties.Empty, BillingAccount, "COP",
            customerVatSurplus: false, customerWithholdings: [], MunicipalContext.None, member, Now, QuotationKind.Project);
        quotation.ReplaceProjectScope(
            ProjectScope.Create(serviceVariantId, ProjectPricingMode.Global, price, [], []),
            new ProjectServiceLine(serviceVariantId, Guid.CreateVersion7(), 19, ProductKind.Service, SalesUnit.Hour),
            member,
            Now);
        quotation.ConvertToOrder(member, Now, QuotationProductSnapshot.None);
        return Order.FromQuotation(
            OrderId.New(), quotation.TenantId, "PED-2026-0001", quotation, null, member, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));
    }
}
