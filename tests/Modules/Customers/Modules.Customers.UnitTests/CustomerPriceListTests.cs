using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

public sealed class CustomerPriceListTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly CustomerId CustomerId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateAssignsThePriceListToTheCustomer()
    {
        var priceListId = Guid.CreateVersion7();

        var assignment = CustomerPriceList.Create(
            CustomerPriceListId.New(), TenantId, CustomerId, priceListId, Now);

        Assert.Equal(TenantId, assignment.TenantId);
        Assert.Equal(CustomerId, assignment.CustomerId);
        Assert.Equal(priceListId, assignment.PriceListId);
        Assert.Equal(Now, assignment.CreatedAt);
    }

    [Fact]
    public void CreateRejectsAnEmptyPriceListId()
    {
        var error = Assert.Throws<CustomersDomainException>(() =>
            CustomerPriceList.Create(
                CustomerPriceListId.New(), TenantId, CustomerId, Guid.Empty, Now));

        Assert.Equal("customers.customer.price_list_required", error.Code);
    }
}
